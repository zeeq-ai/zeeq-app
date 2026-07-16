using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for private library document snippets.
/// </summary>
/// <remarks>
/// Implements the reconcile-in-one-transaction contract of <see cref="ISnippetStore{TDocument}"/>,
/// plus the embedding-claim lease and hybrid RRF search added in the embedding-pipeline slice.
/// Reconciliation matches stored rows to freshly composed snippets by <c>(Kind, ContentHash,
/// Ordinal)</c> so an unchanged payload keeps its embedding.
/// <para>
/// <paramref name="searchScope"/> gates the code-review exclusion filter in
/// <see cref="SearchAsync"/>: on the code-review execution path, snippets whose owning document
/// is flagged <see cref="LibraryDocument.ExcludedFromCodeReviews"/> are filtered in every
/// retrieval arm (the search SQL already joins <c>docs_library_documents</c>, so no snippet-row
/// denormalization is needed).
/// </para>
/// </remarks>
internal sealed class PostgresLibraryDocumentSnippetStore(
    PostgresDbContext db,
    DocumentSearchScope searchScope
) : ISnippetStore<LibraryDocument>
{
    // RRF: score = sum(1 / (k + rank)) across arms, plus an identifier-overlap boost worth one
    // first-rank arm hit. 50 candidates per arm keeps both the HNSW and GIN scans bounded.
    private const int RrfK = 60;
    private const int CandidatesPerArm = 50;

    // ts_rank_cd normalization: 4 (DivideByMeanHarmonicDistanceBetweenExtents) | 32
    // (DivideByItselfPlusOne) — mirrors PostgresLibraryDocumentStore.SearchAsync's document-level
    // ranking so snippet and document search scores are computed the same way.
    private const int FtsRankNormalization = 36;

    /// <inheritdoc />
    public async Task ReplaceForDocumentAsync(
        LibraryDocument document,
        IReadOnlyList<ComposedSnippet> snippets,
        CancellationToken ct
    )
    {
        // Load the existing snippet set for this document (tracked — we mutate/delete it below).
        var existing = await db
            .LibraryDocumentSnippets.TagWithOperationCallSite(
                "documents.library_document_snippet.reconcile_load"
            )
            .Where(snippet =>
                snippet.OrganizationId == document.OrganizationId
                && snippet.LibraryId == document.LibraryId
                && snippet.DocumentId == document.Id
            )
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        // Index existing rows by the reconciliation key so each composed snippet can find its match
        // in O(1). Duplicate identical payloads within a document are disambiguated by Ordinal, so
        // the composite key is unique per document.
        var existingByKey = existing.ToDictionary(snippet =>
            (snippet.Kind, snippet.ContentHash, snippet.Ordinal)
        );

        var matchedKeys = new HashSet<(SnippetKind, string, int)>();

        foreach (var composed in snippets)
        {
            var key = (composed.Kind, composed.ContentHash, composed.Ordinal);

            if (existingByKey.TryGetValue(key, out var stored))
            {
                // Unchanged payload: keep the row (and its embedding) as-is. Metadata that is not
                // part of the hash (header/heading path/etc.) is already identical by construction,
                // since the payload — which includes them — hashed the same.
                //
                // NOTE: EmbeddingPayload is still refreshed here even though ContentHash matching
                // already guarantees it is byte-identical (ContentHash = SHA256(payload)) — this is
                // the fix for a real bug found live (2026-07-11, code review follow-up): the
                // AddSnippetEmbeddingPayload migration backfilled every pre-existing row's column to
                // an empty string, and because this branch never touched EmbeddingPayload, rows
                // whose content never changed since kept that empty string forever — the embedding
                // pipeline then embedded empty payloads for the entire pre-existing corpus without
                // any error (Fireworks returns a normal-looking vector for empty input). Assigning
                // it unconditionally here is a zero-cost no-op for already-correct rows and
                // self-heals this exact class of bug for any future column addition.
                stored.EmbeddingPayload = composed.EmbeddingPayload;
                // Identifiers is the same class of bug: it is derived from Content via
                // SnippetIdentifierExtractor, not from ContentHash, so an extractor logic change
                // (e.g. adding a reserved-word blocklist) never reaches already-stored rows whose
                // payload hash is unchanged unless it is reassigned here too.
                stored.Identifiers = composed.Identifiers;
                matchedKeys.Add(key);
                continue;
            }

            // New snippet: insert with a null embedding for the pipeline to fill in later.
            db.LibraryDocumentSnippets.Add(
                new LibraryDocumentSnippet
                {
                    Id = NewId(),
                    OrganizationId = document.OrganizationId,
                    TeamId = document.TeamId,
                    LibraryId = document.LibraryId,
                    DocumentId = document.Id,
                    Kind = composed.Kind,
                    Header = composed.Header,
                    HeadingPath = composed.HeadingPath,
                    Language = composed.Language,
                    Tag = composed.Tag,
                    PrecedingText = composed.PrecedingText,
                    Content = composed.Content,
                    Identifiers = composed.Identifiers,
                    EmbeddingPayload = composed.EmbeddingPayload,
                    ContentHash = composed.ContentHash,
                    Ordinal = composed.Ordinal,
                    TokenCount = composed.TokenCount,
                    Embedding = null,
                    EmbeddingModel = null,
                    EmbeddingStartedAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                }
            );
        }

        // Delete stored rows the composer no longer produces (removed sections/code).
        foreach (var stored in existing)
        {
            if (!matchedKeys.Contains((stored.Kind, stored.ContentHash, stored.Ordinal)))
            {
                db.LibraryDocumentSnippets.Remove(stored);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbeddingClaim>> ClaimMissingEmbeddingsAsync(
        string embeddingModel,
        TimeSpan lease,
        int limit,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var leaseCutoff = now - lease;

        // Atomic claim via a durable lease column, not a transaction-scoped row lock — the async
        // embedding provider call outlives any single transaction. IS DISTINCT FROM correctly
        // treats a null embedding_model (never embedded) and a stale one (model/dimension change)
        // the same way: both are claimable. FOR UPDATE SKIP LOCKED partitions the backlog safely
        // across concurrent replicas, same pattern as ClaimPendingIndexingAsync.
        //
        // NOTE: the `claimed` CTE's FOR UPDATE SKIP LOCKED row locks are held for the statement's
        // duration and protect the outer UPDATE ... FROM claimed, so this is atomic (equivalent to
        // the WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED) form, not a separate
        // select-then-update race). Verified live: 20 concurrent workers × 5 claim rounds against
        // 200 seeded rows produced zero double-claims (2026-07-11, code review follow-up).
        //
        // NOTE: RETURNING claimed.id, claimed.embedding_payload reads from the CTE, not the
        // updated target row — this is standard Postgres: RETURNING may reference any column
        // visible in the statement's FROM list, and a CTE is just an inline view there. Verified
        // by the ClaimMissingEmbeddings* tests in SnippetStoreIntegrationTests, which exercise
        // this exact query against real Postgres (code review follow-up, 2026-07-11).
        //
        // NOTE: this store intentionally mirrors PostgresPublicDocumentSnippetStore's claim/set/
        // release/search implementations near-verbatim rather than sharing an abstraction — same
        // precedent as ClaimPendingIndexingAsync in PostgresLibraryDocumentStore /
        // PostgresDocsPublicDocumentStore, which documents the same deliberate choice.
        //
        // NOTE: embedding_payload <> '' guards against ever again silently embedding blank content
        // — found live (2026-07-11): a migration-backfilled empty payload was claimed and sent to
        // the provider without error (it returns a normal-looking vector for empty input), so the
        // vector arm silently ran on content-free embeddings for the whole pre-existing corpus. A
        // row with a genuinely blank payload now simply stays unclaimed (FTS-only) instead of
        // corrupting the vector index — see BackfillSnippetEmbeddingPayloadOnStaleDocuments and the
        // ReplaceForDocumentAsync fix that closes the root cause of the payload ever being blank.
        return await db
            .Database.SqlQuery<EmbeddingClaim>(
                $"""
                WITH claimed AS (
                    SELECT id, embedding_payload
                    FROM zeeq.docs_library_document_snippets
                    WHERE (embedding IS NULL OR embedding_model IS DISTINCT FROM {embeddingModel})
                      AND (embedding_started_at IS NULL OR embedding_started_at < {leaseCutoff})
                      AND embedding_payload <> ''
                    ORDER BY updated_at ASC, id ASC
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE zeeq.docs_library_document_snippets t
                SET embedding_started_at = {now}
                FROM claimed
                WHERE t.id = claimed.id
                RETURNING claimed.id, claimed.embedding_payload
                """
            )
            .TagWithOperationCallSite("documents.library_document_snippet.claim_missing_embeddings")
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetEmbeddingsAsync(
        IReadOnlyList<EmbeddingResult> results,
        string embeddingModel,
        CancellationToken ct
    )
    {
        if (results.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var ids = results.Select(result => result.Id).ToArray();
        var embeddings = results.Select(result => result.Embedding).ToArray();

        // Single batched UPDATE via UNNEST — one round trip per embedding batch rather than one
        // per result. Verified live: a parallel-array UNNEST(text[], halfvec[]) join correctly
        // binds distinct vectors per row (2026-07-11, code review follow-up; this replaced an
        // earlier per-result ExecuteUpdateAsync loop flagged independently by three reviewers).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE zeeq.docs_library_document_snippets AS t
            SET embedding = u.embedding,
                embedding_model = {embeddingModel},
                embedding_started_at = NULL,
                updated_at = {now}
            FROM UNNEST({ids}::text[], {embeddings}::halfvec(768)[]) AS u(id, embedding)
            WHERE t.id = u.id
            """,
            ct
        );
    }

    /// <inheritdoc />
    public Task ReleaseEmbeddingClaimsAsync(
        IReadOnlyList<string> snippetIds,
        CancellationToken ct
    ) =>
        db
            .LibraryDocumentSnippets.Where(snippet => snippetIds.Contains(snippet.Id))
            .ExecuteUpdateAsync(
                // NOTE: clearing the lease makes the row immediately reclaimable on the very next
                // sweep tick — this is intentional, not a bug (flagged by one reviewer). The
                // durable-lease design's whole point is "release, then let the next tick retry
                // right away" rather than making a failed batch wait out the full lease duration;
                // ReleaseEmbeddingClaims_ClearsLeaseWithoutWritingVector asserts this directly.
                setters =>
                    setters.SetProperty(
                        snippet => snippet.EmbeddingStartedAt,
                        (DateTimeOffset?)null
                    ),
                ct
            );

    /// <inheritdoc />
    public async Task<IReadOnlyList<SnippetSearchRow>> SearchAsync(
        SnippetSearchQuery query,
        CancellationToken ct
    )
    {
        var organizationId = query.OrganizationId!;
        var libraryId = query.LibraryId!;
        var kind = query.Kind.ToString();
        var excludedPaths = query.ExcludedDocumentPaths;
        var queryIdentifiers = query.QueryIdentifiers;

        // Code-review execution never surfaces snippets of excluded (operational/informational)
        // documents. Bound as a plain bool parameter inside each retrieval arm so both modes
        // share one SQL shape; the arms already join the owning document row `d`.
        var excludeCodeReviewExcluded = searchScope.ForCodeReviewExecution;

        // SET LOCAL only scopes to the current transaction, so it and the search SELECT must run
        // as one transaction — a second, separate command would not see the setting. If the caller
        // is already inside a transaction (e.g. a test fixture's rollback-isolation wrapper, or a
        // future caller composing this into a larger unit of work), Npgsql/EF refuses a nested
        // BeginTransactionAsync — so only open one here when the connection doesn't already have
        // one; compose with an existing ambient transaction otherwise. Read-only, so an owned
        // transaction is never explicitly committed; it disposes (implicit rollback) once the
        // results are materialized.
        var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        await using var _ = ownedTransaction;

        // Iterative scan: keeps walking the HNSW graph until enough rows survive the
        // org/library/kind/exclusion filters — without this, a narrow (e.g. small-tenant) scope
        // can get zero results from a default ef_search=40 candidate walk (pgvector post-filters).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SET LOCAL hnsw.iterative_scan = 'relaxed_order'; SET LOCAL hnsw.ef_search = 100;",
            ct
        );

        var rows = query.QueryEmbedding is { } queryEmbedding
            ? await db
                .Database.SqlQuery<SnippetSearchRow>(
                    $"""
                    WITH vec AS (
                        SELECT s.id, ROW_NUMBER() OVER (ORDER BY s.embedding <=> {queryEmbedding}) AS rnk
                        FROM zeeq.docs_library_document_snippets s
                        JOIN zeeq.docs_library_documents d
                          ON d.organization_id = s.organization_id
                         AND d.library_id = s.library_id
                         AND d.id = s.document_id
                        WHERE s.organization_id = {organizationId}
                          AND s.library_id = {libraryId}
                          AND s.kind = {kind}
                          AND s.embedding IS NOT NULL
                          AND NOT (d.path = ANY({excludedPaths}))
                          AND (NOT {excludeCodeReviewExcluded} OR NOT d.excluded_from_code_reviews)
                        ORDER BY s.embedding <=> {queryEmbedding}
                        LIMIT {CandidatesPerArm}
                    ),
                    fts AS (
                        SELECT s.id, ROW_NUMBER() OVER (
                            ORDER BY ts_rank_cd(
                                ARRAY[0.1,0.2,0.4,1.0], s.search_vector,
                                websearch_to_tsquery('english', {query.QueryText}), {FtsRankNormalization}
                            ) DESC
                        ) AS rnk
                        FROM zeeq.docs_library_document_snippets s
                        JOIN zeeq.docs_library_documents d
                          ON d.organization_id = s.organization_id
                         AND d.library_id = s.library_id
                         AND d.id = s.document_id
                        WHERE s.organization_id = {organizationId}
                          AND s.library_id = {libraryId}
                          AND s.kind = {kind}
                          AND s.search_vector @@ websearch_to_tsquery('english', {query.QueryText})
                          AND NOT (d.path = ANY({excludedPaths}))
                          AND (NOT {excludeCodeReviewExcluded} OR NOT d.excluded_from_code_reviews)
                        LIMIT {CandidatesPerArm}
                    )
                    SELECT s.id AS snippet_id, s.document_id, d.path AS document_path,
                           d.title AS document_title, s.header, s.heading_path, s.language,
                           s.tag, s.content, s.token_count,
                           COALESCE(1.0 / ({RrfK} + vec.rnk), 0)
                         + COALESCE(1.0 / ({RrfK} + fts.rnk), 0)
                         + CASE WHEN s.identifiers && {queryIdentifiers}
                                THEN 1.0 / {RrfK} ELSE 0 END AS score,
                           COALESCE(vec.rnk, 0)::int AS vector_rank,
                           COALESCE(fts.rnk, 0)::int AS text_rank,
                           (s.identifiers && {queryIdentifiers}) AS identifier_match
                    FROM vec FULL OUTER JOIN fts USING (id)
                    JOIN zeeq.docs_library_document_snippets s ON s.id = COALESCE(vec.id, fts.id)
                    JOIN zeeq.docs_library_documents d
                      ON d.organization_id = s.organization_id AND d.library_id = s.library_id
                     AND d.id = s.document_id
                    ORDER BY score DESC, s.id
                    LIMIT {query.Limit}
                    """
                )
                .ToListAsync(ct)
            : await db
                .Database.SqlQuery<SnippetSearchRow>(
                    // NOTE: `score` is a window function (ROW_NUMBER() OVER (...)) embedded
                    // directly in the SELECT list, then referenced by output alias in
                    // ORDER BY — both are valid Postgres (unlike engines that reject alias
                    // reuse within the same SELECT). Verified live via
                    // Search_FtsOnly_ReturnsMatch_WhenQueryEmbeddingIsNull, which exercises
                    // exactly this degraded (embeddings-unavailable) branch against real
                    // Postgres (code review follow-up, 2026-07-11).
                    $"""
                    WITH fts AS (
                        SELECT s.id, ROW_NUMBER() OVER (
                            ORDER BY ts_rank_cd(
                                ARRAY[0.1,0.2,0.4,1.0], s.search_vector,
                                websearch_to_tsquery('english', {query.QueryText}), {FtsRankNormalization}
                            ) DESC
                        ) AS rnk
                        FROM zeeq.docs_library_document_snippets s
                        JOIN zeeq.docs_library_documents d
                          ON d.organization_id = s.organization_id
                         AND d.library_id = s.library_id
                         AND d.id = s.document_id
                        WHERE s.organization_id = {organizationId}
                          AND s.library_id = {libraryId}
                          AND s.kind = {kind}
                          AND s.search_vector @@ websearch_to_tsquery('english', {query.QueryText})
                          AND NOT (d.path = ANY({excludedPaths}))
                          AND (NOT {excludeCodeReviewExcluded} OR NOT d.excluded_from_code_reviews)
                    )
                    SELECT s.id AS snippet_id, s.document_id, d.path AS document_path,
                           d.title AS document_title, s.header, s.heading_path, s.language,
                           s.tag, s.content, s.token_count,
                           (1.0 / ({RrfK} + fts.rnk))
                         + CASE WHEN s.identifiers && {queryIdentifiers}
                                THEN 1.0 / {RrfK} ELSE 0 END AS score,
                           0 AS vector_rank,
                           fts.rnk::int AS text_rank,
                           (s.identifiers && {queryIdentifiers}) AS identifier_match
                    FROM fts
                    JOIN zeeq.docs_library_document_snippets s ON s.id = fts.id
                    JOIN zeeq.docs_library_documents d
                      ON d.organization_id = s.organization_id AND d.library_id = s.library_id
                     AND d.id = s.document_id
                    ORDER BY score DESC, s.id
                    LIMIT {query.Limit}
                    """
                )
                .ToListAsync(ct);

        return rows;
    }

    /// <summary>Mints a new snippet id, e.g. <c>snip_0192f0c1e3a97d4e...</c>.</summary>
    private static string NewId() => $"snip_{Guid.CreateVersion7():N}";
}
