using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

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
        existing.ActiveSyncRunId = source.ActiveSyncRunId;
        existing.ActiveSyncRunCreatedAtUtc = source.ActiveSyncRunCreatedAtUtc;
        existing.SyncQueuedAtUtc = source.SyncQueuedAtUtc;
        existing.SyncStartedAtUtc = source.SyncStartedAtUtc;
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
        // NOTE: This value is both persisted in PostgreSQL and sent on the
        // queue message as the active-run identity. PostgreSQL stores
        // timestamptz at microsecond precision, while .NET ticks are 100ns.
        var now = PostgresTimestampPrecision.TruncateToMicroseconds(DateTimeOffset.UtcNow);

        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var claimed = await db
            .DocsPublicSources.FromSql(
                $"""
                SELECT * FROM zeeq.docs_public_sources
                WHERE sync_status = 'idle'
                  AND status = 'active'
                  AND next_sync_at IS NOT NULL
                  AND next_sync_at <= {now}
                ORDER BY next_sync_at ASC
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """
            )
            .TagWithOperationCallSite("docs_public_source.claim_due_for_sync")
            .ToArrayAsync(ct);

        foreach (var source in claimed)
        {
            source.SyncStatus = "queued";
            source.ActiveSyncRunId = $"run_{Guid.CreateVersion7():N}";
            source.ActiveSyncRunCreatedAtUtc = now;
            source.SyncQueuedAtUtc = now;
            source.SyncStartedAtUtc = null;
            source.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
        return claimed;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateCurrentSyncLeaseAsync(
        string sourceId,
        string expectedRunId,
        DateTimeOffset expectedRunCreatedAtUtc,
        string syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset? syncedAt,
        string status,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        DateTimeOffset updatedAt,
        CancellationToken ct
    )
    {
        var updated = await db
            .DocsPublicSources.TagWithOperationCallSite(
                "docs_public_source.try_update_current_sync_lease"
            )
            .Where(source =>
                source.Id == sourceId
                && source.ActiveSyncRunId == expectedRunId
                && source.ActiveSyncRunCreatedAtUtc == expectedRunCreatedAtUtc
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(source => source.SyncStatus, syncStatus)
                        .SetProperty(source => source.NextSyncAt, nextSyncAt)
                        .SetProperty(source => source.SyncedAt, syncedAt)
                        .SetProperty(source => source.Status, status)
                        .SetProperty(source => source.ActiveSyncRunId, activeSyncRunId)
                        .SetProperty(
                            source => source.ActiveSyncRunCreatedAtUtc,
                            activeSyncRunCreatedAtUtc
                        )
                        .SetProperty(source => source.SyncQueuedAtUtc, syncQueuedAtUtc)
                        .SetProperty(source => source.SyncStartedAtUtc, syncStartedAtUtc)
                        .SetProperty(source => source.UpdatedAt, updatedAt),
                ct
            );

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StalledSyncReset>> ResetStalledSyncsAsync(
        DateTimeOffset now,
        TimeSpan queuedStaleAfter,
        TimeSpan runningStaleAfter,
        int limit,
        CancellationToken ct
    )
    {
        now = PostgresTimestampPrecision.TruncateToMicroseconds(now);
        var queuedCutoff = now - queuedStaleAfter;
        var runningCutoff = now - runningStaleAfter;

        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var stalled = await db
            .DocsPublicSources.FromSql(
                $"""
                SELECT * FROM zeeq.docs_public_sources
                WHERE (
                    sync_status = 'queued'
                    AND sync_queued_at_utc IS NOT NULL
                    AND sync_queued_at_utc <= {queuedCutoff}
                    AND active_sync_run_id IS NOT NULL
                    AND active_sync_run_created_at_utc IS NOT NULL
                  )
                  OR (
                    sync_status = 'running'
                    AND sync_started_at_utc IS NOT NULL
                    AND sync_started_at_utc <= {runningCutoff}
                    AND active_sync_run_id IS NOT NULL
                    AND active_sync_run_created_at_utc IS NOT NULL
                  )
                ORDER BY coalesce(sync_started_at_utc, sync_queued_at_utc) ASC
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """
            )
            .TagWithOperationCallSite("docs_public_source.reset_stalled_syncs")
            .ToArrayAsync(ct);

        var resets = new List<StalledSyncReset>(stalled.Length);
        foreach (var source in stalled)
        {
            var reset = ToStalledSyncReset(source);
            var marked = await MarkRunStalledAsync(
                reset,
                now,
                "Repository sync was reset after exceeding the stalled sync timeout.",
                ct
            );
            if (!marked)
            {
                continue;
            }

            // NOTE: Keep the run status transition and source lease clear in one
            // transaction. Clearing the source first can orphan an active run row
            // that no later sweep can correlate back to this source.
            ClearSyncState(source, now);
            resets.Add(reset);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
        return resets;
    }

    private static StalledSyncReset ToStalledSyncReset(DocsPublicSource source) =>
        new(
            RepositorySourceKind.Public,
            source.Id,
            null,
            null,
            source.ActiveSyncRunId,
            source.ActiveSyncRunCreatedAtUtc
        );

    private async Task<bool> MarkRunStalledAsync(
        StalledSyncReset reset,
        DateTimeOffset completedAtUtc,
        string failureMessage,
        CancellationToken ct
    )
    {
        if (reset.RunId is null || reset.RunCreatedAtUtc is null)
        {
            return false;
        }

        var updated = await db
            .DocsIngestRuns.TagWithOperationCallSite("docs_public_source.mark_stalled_run")
            .Where(run =>
                run.Id == reset.RunId
                && run.CreatedAtUtc == reset.RunCreatedAtUtc
                && run.Status == IngestRunStatus.Running
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(run => run.Status, IngestRunStatus.Stalled)
                        .SetProperty(run => run.FailureMessage, failureMessage)
                        .SetProperty(run => run.CompletedAtUtc, completedAtUtc)
                        .SetProperty(run => run.UpdatedAtUtc, completedAtUtc),
                ct
            );

        return updated > 0;
    }

    private static void ClearSyncState(DocsPublicSource source, DateTimeOffset now)
    {
        source.SyncStatus = "idle";
        source.NextSyncAt = now;
        source.ActiveSyncRunId = null;
        source.ActiveSyncRunCreatedAtUtc = null;
        source.SyncQueuedAtUtc = null;
        source.SyncStartedAtUtc = null;
        source.UpdatedAt = now;
    }
}
