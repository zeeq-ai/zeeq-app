using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for global public document snippets.
/// </summary>
/// <remarks>
/// The public-source counterpart to <see cref="PostgresLibraryDocumentSnippetStore"/>: same
/// reconcile-by-<c>(Kind, ContentHash, Ordinal)</c> logic and the same embedding-claim/search
/// contract, scoped by <c>public_source_id</c> instead of org/library.
/// </remarks>
internal sealed class PostgresPublicDocumentSnippetStore(PostgresDbContext db)
    : ISnippetStore<DocsPublicDocument>
{
    private const int RrfK = 60;
    private const int CandidatesPerArm = 50;
    private const int FtsRankNormalization = 36;

    /// <inheritdoc />
    public async Task ReplaceForDocumentAsync(
        DocsPublicDocument document,
        IReadOnlyList<ComposedSnippet> snippets,
        CancellationToken ct
    )
    {
        var existing = await db
            .PublicDocumentSnippets.TagWithOperationCallSite(
                "documents.public_document_snippet.reconcile_load"
            )
            .Where(snippet =>
                snippet.PublicSourceId == document.PublicSourceId
                && snippet.DocumentId == document.Id
            )
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        // NOTE: ToDictionary assumes (Kind, ContentHash, Ordinal) is unique per document.
        // SnippetComposer guarantees this by construction — it assigns Ordinal specifically to
        // disambiguate repeated ContentHash values within one composed set — and every stored row
        // originated from that same composer, so a duplicate key here would indicate a composer
        // bug, not a case to defend against here.
        var existingByKey = existing.ToDictionary(snippet =>
            (snippet.Kind, snippet.ContentHash, snippet.Ordinal)
        );

        var matchedKeys = new HashSet<(SnippetKind, string, int)>();

        foreach (var composed in snippets)
        {
            var key = (composed.Kind, composed.ContentHash, composed.Ordinal);

            if (existingByKey.TryGetValue(key, out var stored))
            {
                // NOTE: EmbeddingPayload is still refreshed here even though ContentHash matching
                // already guarantees it is byte-identical (ContentHash = SHA256(payload)) — see the
                // matching NOTE on PostgresLibraryDocumentSnippetStore.ReplaceForDocumentAsync for
                // the live bug this fixes (2026-07-11, code review follow-up).
                stored.EmbeddingPayload = composed.EmbeddingPayload;
                // Same class of bug for Identifiers — see the matching NOTE on
                // PostgresLibraryDocumentSnippetStore.ReplaceForDocumentAsync.
                stored.Identifiers = composed.Identifiers;
                matchedKeys.Add(key);
                continue;
            }

            db.PublicDocumentSnippets.Add(
                new PublicDocumentSnippet
                {
                    Id = NewId(),
                    PublicSourceId = document.PublicSourceId,
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

        foreach (var stored in existing)
        {
            if (!matchedKeys.Contains((stored.Kind, stored.ContentHash, stored.Ordinal)))
                db.PublicDocumentSnippets.Remove(stored);
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

        // NOTE: the `claimed` CTE's FOR UPDATE SKIP LOCKED row locks are held for the statement's
        // duration and protect the outer UPDATE ... FROM claimed, so this is atomic — see the
        // matching NOTE on PostgresLibraryDocumentSnippetStore.ClaimMissingEmbeddingsAsync for the
        // live concurrency verification (2026-07-11, code review follow-up).
        //
        // NOTE: RETURNING claimed.* and the near-identical shape of the private store's claim
        // query are both intentional — see the matching NOTEs on
        // PostgresLibraryDocumentSnippetStore.ClaimMissingEmbeddingsAsync.
        //
        // NOTE: embedding_payload <> '' guards against silently embedding blank content — see the
        // matching NOTE on PostgresLibraryDocumentSnippetStore.ClaimMissingEmbeddingsAsync for the
        // live bug this closes (2026-07-11).
        return await db
            .Database.SqlQuery<EmbeddingClaim>(
                $"""
                WITH claimed AS (
                    SELECT id, embedding_payload
                    FROM zeeq.docs_public_document_snippets
                    WHERE (embedding IS NULL OR embedding_model IS DISTINCT FROM {embeddingModel})
                      AND (embedding_started_at IS NULL OR embedding_started_at < {leaseCutoff})
                      AND embedding_payload <> ''
                    ORDER BY updated_at ASC, id ASC
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE zeeq.docs_public_document_snippets t
                SET embedding_started_at = {now}
                FROM claimed
                WHERE t.id = claimed.id
                RETURNING claimed.id, claimed.embedding_payload
                """
            )
            .TagWithOperationCallSite("documents.public_document_snippet.claim_missing_embeddings")
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

        // Single batched UPDATE via UNNEST — see
        // PostgresLibraryDocumentSnippetStore.SetEmbeddingsAsync for the verification note.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE zeeq.docs_public_document_snippets AS t
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
            .PublicDocumentSnippets.Where(snippet => snippetIds.Contains(snippet.Id))
            .ExecuteUpdateAsync(
                // NOTE: immediately reclaimable on the next tick by design — see
                // PostgresLibraryDocumentSnippetStore.ReleaseEmbeddingClaimsAsync.
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
        var publicSourceId = query.PublicSourceId!;
        var kind = query.Kind.ToString();
        var excludedPaths = query.ExcludedDocumentPaths;
        var queryIdentifiers = query.QueryIdentifiers;

        // See PostgresLibraryDocumentSnippetStore.SearchAsync for why this composes with an
        // ambient transaction instead of always opening its own.
        var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        await using var _ = ownedTransaction;

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
                        FROM zeeq.docs_public_document_snippets s
                        JOIN zeeq.docs_public_documents d ON d.id = s.document_id
                        WHERE s.public_source_id = {publicSourceId}
                          AND s.kind = {kind}
                          AND s.embedding IS NOT NULL
                          AND NOT (d.path = ANY({excludedPaths}))
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
                        FROM zeeq.docs_public_document_snippets s
                        JOIN zeeq.docs_public_documents d ON d.id = s.document_id
                        WHERE s.public_source_id = {publicSourceId}
                          AND s.kind = {kind}
                          AND s.search_vector @@ websearch_to_tsquery('english', {query.QueryText})
                          AND NOT (d.path = ANY({excludedPaths}))
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
                    JOIN zeeq.docs_public_document_snippets s ON s.id = COALESCE(vec.id, fts.id)
                    JOIN zeeq.docs_public_documents d ON d.id = s.document_id
                    ORDER BY score DESC, s.id
                    LIMIT {query.Limit}
                    """
                )
                .ToListAsync(ct)
            : await db
                .Database.SqlQuery<SnippetSearchRow>(
                    $"""
                    SELECT s.id AS snippet_id, s.document_id, d.path AS document_path,
                           d.title AS document_title, s.header, s.heading_path, s.language,
                           s.tag, s.content, s.token_count,
                           (1.0 / ({RrfK} + ROW_NUMBER() OVER (
                               ORDER BY ts_rank_cd(
                                   ARRAY[0.1,0.2,0.4,1.0], s.search_vector,
                                   websearch_to_tsquery('english', {query.QueryText}), {FtsRankNormalization}
                               ) DESC
                           )))
                         + CASE WHEN s.identifiers && {queryIdentifiers}
                                THEN 1.0 / {RrfK} ELSE 0 END AS score,
                           0 AS vector_rank,
                           ROW_NUMBER() OVER (
                               ORDER BY ts_rank_cd(
                                   ARRAY[0.1,0.2,0.4,1.0], s.search_vector,
                                   websearch_to_tsquery('english', {query.QueryText}), {FtsRankNormalization}
                               ) DESC
                           )::int AS text_rank,
                           (s.identifiers && {queryIdentifiers}) AS identifier_match
                    FROM zeeq.docs_public_document_snippets s
                    JOIN zeeq.docs_public_documents d ON d.id = s.document_id
                    WHERE s.public_source_id = {publicSourceId}
                      AND s.kind = {kind}
                      AND s.search_vector @@ websearch_to_tsquery('english', {query.QueryText})
                      AND NOT (d.path = ANY({excludedPaths}))
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
