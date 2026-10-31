using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

// Operational sync/indexing claims, leases and recovery; these stamps are separate from audited content changes.
internal sealed partial class PostgresLibraryDocumentStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct)
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
            .Libraries.FromSql(
                $"""
                SELECT * FROM zeeq.docs_libraries
                WHERE sync_status = 'idle'
                  AND source_kind = 'GitHub'
                  AND next_sync_at IS NOT NULL
                  AND next_sync_at <= {now}
                ORDER BY next_sync_at ASC
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """
            )
            .TagWithOperationCallSite("documents.library.claim_due_for_sync")
            .ToArrayAsync(ct);

        foreach (var library in claimed)
        {
            library.SyncStatus = "queued";
            library.ActiveSyncRunId = $"run_{Guid.CreateVersion7():N}";
            library.ActiveSyncRunCreatedAtUtc = now;
            library.SyncQueuedAtUtc = now;
            library.SyncStartedAtUtc = null;
            library.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
        return claimed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> ClaimDueNotionSyncAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        now = PostgresTimestampPrecision.TruncateToMicroseconds(now);
        var notionKind = RepositorySourceKind.Notion.ToString();
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var claimed = await db
            .Libraries.FromSql(
                $"""
                SELECT * FROM zeeq.docs_libraries
                WHERE sync_status = 'idle'
                  AND source_kind = {notionKind}
                  AND (
                    (next_sync_at IS NOT NULL AND next_sync_at <= {now})
                    OR
                    (next_full_resync_at IS NOT NULL AND next_full_resync_at <= {now})
                  )
                ORDER BY LEAST(
                    COALESCE(next_sync_at, 'infinity'::timestamptz),
                    COALESCE(next_full_resync_at, 'infinity'::timestamptz)
                ) ASC
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """
            )
            .TagWithOperationCallSite("documents.library.claim_due_notion_sync")
            .ToArrayAsync(ct);

        foreach (var library in claimed)
        {
            library.SyncStatus = "queued";
            library.ActiveSyncRunId = $"run_{Guid.CreateVersion7():N}";
            library.ActiveSyncRunCreatedAtUtc = now;
            library.SyncQueuedAtUtc = now;
            library.SyncStartedAtUtc = null;
            library.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return claimed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now - staleAfter;

        // Atomic claim, then load. The UPDATE ... RETURNING id is a single statement, so concurrent
        // workers still partition the backlog via FOR UPDATE SKIP LOCKED. We RETURN only id (not *)
        // because LibraryDocument owns a JSON column (SourceOrigin) — EF must compose a projection to
        // read it, and composition over a non-composable UPDATE...RETURNING is rejected by the
        // provider. Loading the just-claimed rows by id in the same transaction is safe: they are
        // already marked Indexing, so no other claimer can match them. Pending/Stale rows are due (a
        // Stale row is a forced re-index); an Indexing row older than the cutoff is a crashed-worker
        // orphan reclaimed here. ORDER BY updated_at ASC drains the oldest backlog first.
        var claimedIds = await db
            .Database.SqlQuery<string>(
                $"""
                UPDATE zeeq.docs_library_documents
                SET processing_status = 'Indexing', updated_at = {now}
                WHERE (organization_id, library_id, id) IN (
                    SELECT organization_id, library_id, id FROM zeeq.docs_library_documents
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
        {
            return [];
        }

        // NOTE: Reload by Id alone is exact here — document ids are globally-unique UUIDv7 values
        // (minted as "document_{Guid.CreateVersion7():N}"), never reused across orgs/libraries — so
        // the reload cannot rehydrate a different tenant's row. The atomic UPDATE above already used
        // the full composite key; this requery only rehydrates the rows it just marked Indexing.
        //
        // AsNoTracking(): the sweep pipeline never mutates these entities through EF — parsing only
        // reads Content/Title, and the subsequent status stamp goes through SetProcessingStatusAsync's
        // own set-based UPDATE, not a tracked-entity SaveChanges. Tracking here bought nothing but
        // change-tracker overhead on every drained document.
        //
        // FUTURE OPTIMIZATION: this still reloads the full entity (Keywords, Headings, PreviousPaths,
        // the owned SourceOrigin JSON, the computed SearchVector tsvector) when the pipeline only
        // needs Content/Title plus the key columns. A narrower projection would cut bytes-over-wire
        // and allocation further, but requires changing SetProcessingStatusAsync (and the hosted
        // service's generic pipeline) to accept a narrow shape instead of LibraryDocument — worth
        // doing only if this still shows up in profiling after AsNoTracking().
        return await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.claim_pending_indexing_load")
            .Where(document => claimedIds.Contains(document.Id))
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetProcessingStatusAsync(
        LibraryDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;

        // Set-based UPDATE keyed on the full distribution key — avoids loading and tracking the
        // (large) content column just to flip a status flag.
        await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.set_processing_status"
            )
            .Where(row =>
                row.OrganizationId == document.OrganizationId
                && row.LibraryId == document.LibraryId
                && row.Id == document.Id
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.ProcessingStatus, status)
                        .SetProperty(row => row.UpdatedAt, now),
                ct
            );
    }

    /// <inheritdoc />
    public async Task<Library> UpdateSyncStateAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        CancellationToken ct
    )
    {
        var existing = await db
            .Libraries.TagWithOperationCallSite("documents.library.update_sync_state")
            .SingleAsync(row => row.OrganizationId == organizationId && row.Id == libraryId, ct);

        existing.SyncStatus = syncStatus;
        existing.NextSyncAt = nextSyncAt;
        existing.ManualTriggerHistory = manualTriggerHistory;
        existing.SourceSyncedAt = sourceSyncedAt;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc />
    public async Task<Library> UpdateSyncLeaseAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        CancellationToken ct
    )
    {
        var existing = await db
            .Libraries.TagWithOperationCallSite("documents.library.update_sync_lease")
            .SingleAsync(row => row.OrganizationId == organizationId && row.Id == libraryId, ct);

        existing.SyncStatus = syncStatus;
        existing.NextSyncAt = nextSyncAt;
        existing.ManualTriggerHistory = manualTriggerHistory;
        existing.SourceSyncedAt = sourceSyncedAt;
        existing.ActiveSyncRunId = activeSyncRunId;
        existing.ActiveSyncRunCreatedAtUtc = activeSyncRunCreatedAtUtc;
        existing.SyncQueuedAtUtc = syncQueuedAtUtc;
        existing.SyncStartedAtUtc = syncStartedAtUtc;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateCurrentSyncLeaseAsync(
        string organizationId,
        string libraryId,
        string expectedRunId,
        DateTimeOffset expectedRunCreatedAtUtc,
        string? expectedSyncStatus,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        DateTimeOffset? nextFullResyncAt,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await db
            .Libraries.TagWithOperationCallSite("documents.library.try_update_current_sync_lease")
            .Where(row =>
                row.OrganizationId == organizationId
                && row.Id == libraryId
                && row.ActiveSyncRunId == expectedRunId
                && row.ActiveSyncRunCreatedAtUtc == expectedRunCreatedAtUtc
                && (expectedSyncStatus == null || row.SyncStatus == expectedSyncStatus)
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.SyncStatus, syncStatus)
                        .SetProperty(row => row.NextSyncAt, nextSyncAt)
                        .SetProperty(row => row.ManualTriggerHistory, manualTriggerHistory)
                        .SetProperty(row => row.SourceSyncedAt, sourceSyncedAt)
                        .SetProperty(row => row.ActiveSyncRunId, activeSyncRunId)
                        .SetProperty(
                            row => row.ActiveSyncRunCreatedAtUtc,
                            activeSyncRunCreatedAtUtc
                        )
                        .SetProperty(row => row.SyncQueuedAtUtc, syncQueuedAtUtc)
                        .SetProperty(row => row.SyncStartedAtUtc, syncStartedAtUtc)
                        .SetProperty(row => row.NextFullResyncAt, nextFullResyncAt)
                        .SetProperty(row => row.UpdatedAt, now),
                ct
            );

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<LibrarySyncStateReset?> ResetLibrarySyncStateAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        now = PostgresTimestampPrecision.TruncateToMicroseconds(now);
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var library = await db
            .Libraries.FromSql(
                $"""
                SELECT * FROM zeeq.docs_libraries
                WHERE organization_id = {organizationId}
                  AND id = {libraryId}
                  AND source_kind IS NOT NULL
                  AND sync_status IN ('queued', 'running')
                  AND active_sync_run_id IS NOT NULL
                  AND active_sync_run_created_at_utc IS NOT NULL
                FOR UPDATE
                """
            )
            .TagWithOperationCallSite("documents.library.reset_sync_state_find")
            .SingleOrDefaultAsync(ct);

        if (library is null)
        {
            return null;
        }

        var cleared = ToStalledSyncReset(library);
        var marked = await MarkRunStalledAsync(
            cleared,
            now,
            "Repository sync was manually reset from the library sync status tab.",
            ct
        );
        if (!marked)
        {
            return null;
        }

        // NOTE: Mark the active run while this library row is locked, then clear
        // the lease in the same transaction. Reversing this order can strand an
        // active run with no source row that can recover it later.
        ClearSyncState(library, now);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new LibrarySyncStateReset(library, cleared);
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
            .Libraries.FromSql(
                $"""
                SELECT * FROM zeeq.docs_libraries
                WHERE source_kind IS NOT NULL
                  AND (
                    (sync_status = 'queued'
                     AND sync_queued_at_utc IS NOT NULL
                     AND sync_queued_at_utc <= {queuedCutoff}
                     AND active_sync_run_id IS NOT NULL
                     AND active_sync_run_created_at_utc IS NOT NULL)
                    OR
                    (sync_status = 'running'
                     AND sync_started_at_utc IS NOT NULL
                     AND sync_started_at_utc <= {runningCutoff}
                     AND active_sync_run_id IS NOT NULL
                     AND active_sync_run_created_at_utc IS NOT NULL)
                  )
                ORDER BY coalesce(sync_started_at_utc, sync_queued_at_utc) ASC
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED
                """
            )
            .TagWithOperationCallSite("documents.library.reset_stalled_syncs")
            .ToArrayAsync(ct);

        var resets = new List<StalledSyncReset>(stalled.Length);
        foreach (var library in stalled)
        {
            var reset = ToStalledSyncReset(library);
            var marked = await MarkRunStalledAsync(
                reset,
                now,
                "Content sync was reset after exceeding the stalled sync timeout.",
                ct
            );
            if (!marked)
            {
                continue;
            }

            // NOTE: The run transition and lease clear are intentionally in the
            // same transaction while the source row is locked; otherwise a crash
            // between separate operations can produce an idle library with a
            // permanently active ingest-run record.
            ClearSyncState(library, now);
            resets.Add(reset);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
        return resets;
    }

    private static StalledSyncReset ToStalledSyncReset(Library library) =>
        new(
            library.SourceKind == RepositorySourceKind.Notion.ToString()
                ? RepositorySourceKind.Notion
                : RepositorySourceKind.Private,
            library.Id,
            library.OrganizationId,
            library.Id,
            library.ActiveSyncRunId,
            library.ActiveSyncRunCreatedAtUtc
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
            .DocsIngestRuns.TagWithOperationCallSite("documents.library.mark_stalled_run")
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

    private static void ClearSyncState(Library library, DateTimeOffset now)
    {
        library.SyncStatus = "idle";
        library.NextSyncAt = now;
        library.ActiveSyncRunId = null;
        library.ActiveSyncRunCreatedAtUtc = null;
        library.SyncQueuedAtUtc = null;
        library.SyncStartedAtUtc = null;
        library.UpdatedAt = now;
    }
}
