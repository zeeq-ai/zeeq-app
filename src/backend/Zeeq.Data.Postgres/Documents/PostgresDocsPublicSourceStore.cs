using Zeeq.Core.Documents;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for public repository sources.
/// </summary>
internal sealed class PostgresDocsPublicSourceStore(PostgresDbContext db) : IDocsPublicSourceStore
{
    /// <inheritdoc />
    public Task<DocsPublicSource?> GetByIdAsync(string id, CancellationToken ct) =>
        db
            .DocsPublicSources.TagWithOperationCallSite("docs_public_source.get_by_id")
            .SingleOrDefaultAsync(source => source.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsPublicSource>> GetByIdsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken ct
    ) =>
        await db
            .DocsPublicSources.TagWithOperationCallSite("docs_public_source.get_by_ids")
            .Where(source => ids.Contains(source.Id))
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public Task<DocsPublicSource?> GetByRepoUrlAsync(string repoUrl, CancellationToken ct) =>
        db
            .DocsPublicSources.TagWithOperationCallSite("docs_public_source.get_by_repo_url")
            .SingleOrDefaultAsync(source => source.RepoUrl == repoUrl, ct);

    /// <inheritdoc />
    public async Task<DocsPublicSource> CreateAsync(DocsPublicSource source, CancellationToken ct)
    {
        db.DocsPublicSources.Add(source);
        await db.SaveChangesAsync(ct);
        return source;
    }

    /// <inheritdoc />
    public async Task<DocsPublicSource> UpdateAsync(DocsPublicSource source, CancellationToken ct)
    {
        var existing = await db
            .DocsPublicSources.TagWithOperationCallSite("docs_public_source.update_find")
            .SingleAsync(row => row.Id == source.Id, ct);

        existing.Name = source.Name;
        existing.SyncedAt = source.SyncedAt;
        existing.SyncStatus = source.SyncStatus;
        existing.NextSyncAt = source.NextSyncAt;
        existing.Status = source.Status;
        existing.ManualTriggerHistory = source.ManualTriggerHistory;
        existing.UpdatedAt = source.UpdatedAt;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocsPublicSource>> ClaimDueForSyncAsync(
        int limit,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;

        // Atomic claim: SKIP LOCKED lets concurrent scheduler ticks race safely —
        // a source already locked by another claimant is simply excluded from this
        // batch rather than blocking. UPDATE ... RETURNING makes the claim and the
        // read one round trip.
        return await db
            .DocsPublicSources.FromSql(
                $"""
                UPDATE zeeq.docs_public_sources
                SET sync_status = 'queued'
                WHERE id IN (
                    SELECT id FROM zeeq.docs_public_sources
                    WHERE sync_status = 'idle'
                      AND status = 'active'
                      AND next_sync_at IS NOT NULL
                      AND next_sync_at <= {now}
                    ORDER BY next_sync_at ASC
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING *
                """
            )
            .TagWithOperationCallSite("docs_public_source.claim_due_for_sync")
            .ToArrayAsync(ct);
    }
}
