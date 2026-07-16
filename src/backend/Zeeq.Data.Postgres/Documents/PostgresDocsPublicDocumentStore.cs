using Zeeq.Core.Documents;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for documents ingested from public repository sources.
/// </summary>
internal sealed class PostgresDocsPublicDocumentStore(PostgresDbContext db)
    : IDocsPublicDocumentStore
{
    /// <inheritdoc />
    public async Task<DocsPublicDocumentUpsertResult> UpsertAsync(
        DocsPublicDocument document,
        CancellationToken ct
    )
    {
        var byPath = await db
            .DocsPublicDocuments.TagWithOperationCallSite(
                "docs_public_document.upsert_find_by_path"
            )
            .SingleOrDefaultAsync(
                row => row.PublicSourceId == document.PublicSourceId && row.Path == document.Path,
                ct
            );

        if (byPath is not null)
        {
            if (byPath.ContentHash == document.ContentHash)
            {
                // Content unchanged — re-stamp only. UpdatedAt intentionally does not
                // advance so unrelated observers do not see spurious churn.
                byPath.SyncRunId = document.SyncRunId;
                await db.SaveChangesAsync(ct);
                return new(byPath, DocumentUpsertKind.Unchanged);
            }

            byPath.Content = document.Content;
            byPath.ContentHash = document.ContentHash;
            byPath.Title = document.Title;
            byPath.TitleNormalized = document.TitleNormalized;
            byPath.Keywords = document.Keywords;
            byPath.Headings = document.Headings;
            byPath.TokenCount = document.TokenCount;
            byPath.ProcessingStatus = document.ProcessingStatus;
            byPath.SyncRunId = document.SyncRunId;
            byPath.UpdatedAt = document.UpdatedAt;
            await db.SaveChangesAsync(ct);
            return new(byPath, DocumentUpsertKind.Updated);
        }

        // NOTE: content_hash is not unique per source — two distinct files can
        // legitimately share identical content (copied templates, generated
        // boilerplate). Take(2) instead of SingleOrDefaultAsync so an ambiguous
        // match falls through to Added rather than throwing or guessing which
        // row moved. An exact-path match always wins first (above), so a genuine
        // move is only misclassified as Added in the rare case where the
        // moved file's content also happens to collide with another file's
        // content elsewhere in the same source.
        var hashMatches = await db
            .DocsPublicDocuments.TagWithOperationCallSite(
                "docs_public_document.upsert_find_by_hash"
            )
            .Where(row =>
                row.PublicSourceId == document.PublicSourceId
                && row.ContentHash == document.ContentHash
            )
            .Take(2)
            .ToListAsync(ct);

        if (hashMatches.Count == 1)
        {
            var byHash = hashMatches[0];
            byHash.PreviousPaths = byHash.PreviousPaths.Contains(byHash.Path)
                ? byHash.PreviousPaths
                : [.. byHash.PreviousPaths, byHash.Path];
            byHash.Path = document.Path;
            byHash.SyncRunId = document.SyncRunId;
            byHash.UpdatedAt = document.UpdatedAt;
            await db.SaveChangesAsync(ct);
            return new(byHash, DocumentUpsertKind.Moved);
        }

        db.DocsPublicDocuments.Add(document);
        await db.SaveChangesAsync(ct);
        return new(document, DocumentUpsertKind.Added);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsPublicDocument>> ListBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) =>
        await db
            .DocsPublicDocuments.AsNoTracking()
            .TagWithOperationCallSite("docs_public_document.list_by_source")
            .Where(document => document.PublicSourceId == publicSourceId)
            .OrderBy(document => document.Path)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsPublicDocument>> ListSummariesBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) =>
        await db
            .DocsPublicDocuments.AsNoTracking()
            .TagWithOperationCallSite("docs_public_document.list_summaries_by_source")
            .Where(document => document.PublicSourceId == publicSourceId)
            .OrderBy(document => document.Path)
            // Content is excluded at the SQL level — a shared public source's
            // documents are only ever summarized/filtered here, never rendered
            // in full, so there's no reason to pull every markdown body over
            // the wire just to list titles and paths.
            .Select(document => new DocsPublicDocument
            {
                Id = document.Id,
                PublicSourceId = document.PublicSourceId,
                Path = document.Path,
                Title = document.Title,
                TitleNormalized = document.TitleNormalized,
                Keywords = document.Keywords,
                Headings = document.Headings,
                TokenCount = document.TokenCount,
                ProcessingStatus = document.ProcessingStatus,
                CreatedAt = document.CreatedAt,
                UpdatedAt = document.UpdatedAt,
            })
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<DocsPublicDocument?> GetByPathAsync(
        string publicSourceId,
        string path,
        CancellationToken ct
    )
    {
        var normalized = NormalizePath(path);
        var candidates = BuildPathCandidates(normalized);
        if (candidates.Length == 0)
        {
            return null;
        }

        var exact = await FindByExactPathAsync(publicSourceId, candidates[0], ct);
        if (exact is not null)
        {
            return exact;
        }

        // Suffix match — mirrors PostgresLibraryDocumentStore.GetByPathAsync so a
        // caller can resolve a public-source document by a partial (folder-relative
        // or bare-filename) path, not just its full stored path.
        var suffixMatch = await FindByAnySuffixAsync(publicSourceId, candidates, ct);
        if (suffixMatch is not null)
        {
            return suffixMatch;
        }

        // Fallback: an old alias (D-4 equivalent for public documents) — the
        // caller may be following a stale link to a path this document was
        // renamed away from.
        return await db
            .DocsPublicDocuments.AsNoTracking()
            .TagWithOperationCallSite("docs_public_document.get_by_previous_path")
            .SingleOrDefaultAsync(
                document =>
                    document.PublicSourceId == publicSourceId
                    && document.PreviousPaths.Contains(normalized),
                ct
            );
    }

    private Task<DocsPublicDocument?> FindByExactPathAsync(
        string publicSourceId,
        string path,
        CancellationToken ct
    ) =>
        db
            .DocsPublicDocuments.AsNoTracking()
            .TagWithOperationCallSite("docs_public_document.get_by_exact_path")
            .SingleOrDefaultAsync(
                document => document.PublicSourceId == publicSourceId && document.Path == path,
                ct
            );

    /// <summary>
    /// Finds the best suffix match across all candidates in a single round trip.
    /// See <c>PostgresLibraryDocumentStore.FindByAnySuffixAsync</c> for the
    /// rationale behind the raw-SQL <c>UNNEST … WITH ORDINALITY</c> join.
    /// </summary>
    private Task<DocsPublicDocument?> FindByAnySuffixAsync(
        string publicSourceId,
        string[] candidates,
        CancellationToken ct
    )
    {
        var reversedPatterns = Array.ConvertAll(candidates, static c => Reverse(c) + "%");

        return db
            .DocsPublicDocuments.FromSql(
                $"""
                SELECT d.*
                FROM zeeq.docs_public_documents d
                JOIN UNNEST({reversedPatterns}::text[]) WITH ORDINALITY AS c(pattern, idx)
                  ON d.path_reversed LIKE c.pattern
                WHERE d.public_source_id = {publicSourceId}
                ORDER BY c.idx, d.path
                """
            )
            .AsNoTracking()
            .TagWithOperationCallSite("docs_public_document.get_by_suffix_path_batched")
            .FirstOrDefaultAsync(ct);
    }

    private static string[] BuildPathCandidates(string input)
    {
        var parts = input.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return [];
        }

        var candidates = new string[parts.Length];
        for (var start = 0; start < parts.Length; start++)
        {
            candidates[start] = "/" + string.Join('/', parts[start..]);
        }

        return candidates;
    }

    private static string Reverse(string value) => new(value.Reverse().ToArray());

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    /// <inheritdoc />
    public async Task<int> DeleteUnstampedAsync(
        string publicSourceId,
        string currentSyncRunId,
        CancellationToken ct
    ) =>
        await db
            .DocsPublicDocuments.TagWithOperationCallSite("docs_public_document.delete_unstamped")
            .Where(document =>
                document.PublicSourceId == publicSourceId && document.SyncRunId != currentSyncRunId
            )
            .ExecuteDeleteAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsPublicDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now - staleAfter;

        // Atomic claim, then load — same shape as the private store. The single UPDATE ...
        // RETURNING id statement still partitions the backlog across workers via FOR UPDATE SKIP
        // LOCKED; we then reload the just-claimed rows by id. Kept consistent with the private path
        // (which must avoid FromSql composition over its owned JSON column) rather than relying on
        // FromSql(UPDATE ... RETURNING *) materializing directly, which EF does not guarantee.
        // Same claim semantics: Pending/Stale rows are due, stale Indexing rows are reclaimed
        // (crash recovery), Failed rows are skipped.
        var claimedIds = await db
            .Database.SqlQuery<string>(
                $"""
                UPDATE zeeq.docs_public_documents
                SET processing_status = 'Indexing', updated_at = {now}
                WHERE id IN (
                    SELECT id FROM zeeq.docs_public_documents
                    WHERE processing_status IN ('Pending', 'Stale')
                       OR (processing_status = 'Indexing' AND updated_at < {staleCutoff})
                    ORDER BY updated_at ASC
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id
                """
            )
            .ToArrayAsync(ct);

        if (claimedIds.Length == 0)
            return [];

        // AsNoTracking(): mirrors PostgresLibraryDocumentStore.ClaimPendingIndexingAsync — the sweep
        // pipeline never mutates these entities through EF; SetProcessingStatusAsync below does its
        // own set-based ExecuteUpdateAsync keyed on Id, not a tracked-entity SaveChanges.
        //
        // FUTURE OPTIMIZATION: still reloads the full entity when the pipeline only needs
        // Content/Title plus Id. See the matching NOTE on the private store for the narrower-
        // projection option, deferred pending profiling.
        return await db
            .DocsPublicDocuments.AsNoTracking()
            .TagWithOperationCallSite("docs_public_document.claim_pending_indexing_load")
            .Where(document => claimedIds.Contains(document.Id))
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetProcessingStatusAsync(
        DocsPublicDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;

        await db
            .DocsPublicDocuments.TagWithOperationCallSite(
                "docs_public_document.set_processing_status"
            )
            .Where(row => row.Id == document.Id)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.ProcessingStatus, status)
                        .SetProperty(row => row.UpdatedAt, now),
                ct
            );
    }
}
