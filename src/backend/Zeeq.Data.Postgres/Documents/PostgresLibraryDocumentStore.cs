using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for document libraries and markdown documents.
/// </summary>
/// <remarks>
/// <paramref name="searchScope"/> gates the code-review exclusion filter: when the current DI
/// scope serves a code-review tool invocation (<see cref="DocumentSearchScope.ForCodeReviewExecution"/>),
/// <see cref="SearchAsync"/> and <see cref="ListDocumentsAsync"/> hide documents flagged
/// <see cref="LibraryDocument.ExcludedFromCodeReviews"/>. Path resolution
/// (<see cref="GetByPathAsync"/>) is intentionally unfiltered — an excluded document must still
/// resolve when read directly by path.
/// <para>
/// NOTE: the exclusion policy lives in each query's predicate rather than a centralized query
/// root (code review follow-up, 2026-07-15) — acceptable while only list/search consult it, but
/// any future list/search-shaped query added to this store (or the snippet store) must apply the
/// same <c>!ForCodeReviewExecution || !ExcludedFromCodeReviews</c> predicate, or excluded
/// documents leak back into review results.
/// </para>
/// </remarks>
internal sealed class PostgresLibraryDocumentStore(
    PostgresDbContext db,
    DocumentSearchScope searchScope
) : ILibraryDocumentStore
{
    private static readonly float[] SearchWeights = [0.1f, 0.2f, 0.4f, 1.0f];

    // Flag 4 normalizes for match density; flag 32 (rank/(rank+1)) bounds the score to [0, 1) so it
    // is directly comparable to the [0, 1] trigram similarity used by fuzzy title matching.
    private const NpgsqlTsRankingNormalization SearchNormalization =
        NpgsqlTsRankingNormalization.DivideByMeanHarmonicDistanceBetweenExtents
        | NpgsqlTsRankingNormalization.DivideByItselfPlusOne;

    /// <inheritdoc />
    public Task<Library?> GetLibraryAsync(
        string organizationId,
        string name,
        CancellationToken ct
    ) =>
        db
            .Libraries.TagWithOperationCallSite("documents.library.get")
            .SingleOrDefaultAsync(
                library => library.OrganizationId == organizationId && library.Name == name,
                ct
            );

    /// <inheritdoc />
    public Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) =>
        db
            .Libraries.TagWithOperationCallSite("documents.library.get_by_id")
            .SingleOrDefaultAsync(
                library => library.OrganizationId == organizationId && library.Id == libraryId,
                ct
            );

    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> ListLibrariesAsync(
        string organizationId,
        CancellationToken ct
    ) =>
        await db
            .Libraries.TagWithOperationCallSite("documents.library.list")
            .Where(library => library.OrganizationId == organizationId)
            .OrderBy(library => library.Name)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    ) =>
        await db
            .Libraries.TagWithOperationCallSite("documents.library.list_by_public_source")
            .Where(library => library.PublicSourceId == publicSourceId)
            .ToArrayAsync(ct);

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
                  AND source_kind IS NOT NULL
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
    public async Task<Library> CreateLibraryAsync(Library library, CancellationToken ct)
    {
        db.Libraries.Add(library);
        await db.SaveChangesAsync(ct);
        return library;
    }

    /// <inheritdoc />
    public async Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct)
    {
        var existing = await db
            .Libraries.TagWithOperationCallSite("documents.library.update_find")
            .SingleAsync(
                row => row.OrganizationId == library.OrganizationId && row.Id == library.Id,
                ct
            );

        existing.Name = library.Name;
        existing.Description = library.Description;
        db.Entry(existing).Property(row => row.IncludeFilters).CurrentValue =
            library.IncludeFilters;
        db.Entry(existing).Property(row => row.ExcludeFilters).CurrentValue =
            library.ExcludeFilters;
        existing.UpdatedAt = library.UpdatedAt;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc />
    public async Task DeleteLibraryAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    )
    {
        var library = await db
            .Libraries.TagWithOperationCallSite("documents.library.delete_find")
            .SingleOrDefaultAsync(
                row => row.OrganizationId == organizationId && row.Id == libraryId,
                ct
            );

        if (library is null)
        {
            return;
        }

        // Strip this library id out of every repository that still maps to it,
        // in the same SaveChangesAsync as the library delete (same DbContext, one
        // implicit transaction).
        var referencingRepositories = await db
            .CodeRepositories.TagWithOperationCallSite(
                "documents.library.delete_cascade_repositories"
            )
            .Where(r => r.OrganizationId == organizationId && r.LibraryIds.Contains(libraryId))
            .ToListAsync(ct);

        foreach (var repository in referencingRepositories)
        {
            repository.LibraryIds = repository.LibraryIds.Where(id => id != libraryId).ToArray();
        }

        db.Libraries.Remove(library);
        await db.SaveChangesAsync(ct);
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
        var now = DateTimeOffset.UtcNow;
        var updated = await db
            .Libraries.TagWithOperationCallSite("documents.library.try_update_current_sync_lease")
            .Where(row =>
                row.OrganizationId == organizationId
                && row.Id == libraryId
                && row.ActiveSyncRunId == expectedRunId
                && row.ActiveSyncRunCreatedAtUtc == expectedRunCreatedAtUtc
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
                "Repository sync was reset after exceeding the stalled sync timeout.",
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
            RepositorySourceKind.Private,
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

    /// <inheritdoc />
    public async Task<LibraryDocument> UpsertDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        var existing = await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.upsert_find_existing"
            )
            .SingleOrDefaultAsync(
                row =>
                    row.OrganizationId == document.OrganizationId
                    && row.LibraryId == document.LibraryId
                    && row.Path == document.Path,
                ct
            );

        if (existing is null)
        {
            db.LibraryDocuments.Add(document);
            await db.SaveChangesAsync(ct);
            return document;
        }

        existing.Title = document.Title;
        existing.TitleNormalized = document.TitleNormalized;
        existing.Keywords = document.Keywords;
        existing.Headings = document.Headings;
        existing.Content = document.Content;
        existing.ProcessingStatus = document.ProcessingStatus;
        existing.TokenCount = document.TokenCount;
        existing.ContentHash = document.ContentHash;
        existing.UpdatedAt = document.UpdatedAt;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc />
    public async Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        var byPath = await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.upsert_synced_find_by_path"
            )
            .SingleOrDefaultAsync(
                row =>
                    row.OrganizationId == document.OrganizationId
                    && row.LibraryId == document.LibraryId
                    && row.Path == document.Path,
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

        // See PostgresDocsPublicDocumentStore.UpsertAsync's equivalent note:
        // content_hash is not unique per library, so Take(2) instead of
        // SingleOrDefaultAsync lets an ambiguous match fall through to Added
        // rather than throwing or guessing which row moved.
        var hashMatches = await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.upsert_synced_find_by_hash"
            )
            .Where(row =>
                row.OrganizationId == document.OrganizationId
                && row.LibraryId == document.LibraryId
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

        db.LibraryDocuments.Add(document);
        await db.SaveChangesAsync(ct);
        return new(document, DocumentUpsertKind.Added);
    }

    /// <inheritdoc />
    public async Task<int> DeleteUnstampedAsync(
        string organizationId,
        string libraryId,
        string currentSyncRunId,
        CancellationToken ct
    ) =>
        await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.delete_unstamped"
            )
            .Where(document =>
                document.OrganizationId == organizationId
                && document.LibraryId == libraryId
                && document.SyncRunId != currentSyncRunId
            )
            .ExecuteDeleteAsync(ct);

    /// <inheritdoc />
    public async Task DeleteDocumentAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    )
    {
        var normalizedPath = NormalizeLookupPath(path);
        var document = await db
            .LibraryDocuments.TagWithOperationCallSite("documents.library_document.delete_find")
            .SingleOrDefaultAsync(
                row =>
                    row.OrganizationId == organizationId
                    && row.LibraryId == libraryId
                    && row.Path == normalizedPath,
                ct
            );

        if (document is null)
        {
            return;
        }

        db.LibraryDocuments.Remove(document);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<LibraryDocument?> GetByPathAsync(
        string organizationId,
        string libraryId,
        string input,
        CancellationToken ct
    )
    {
        var normalizedInput = NormalizeLookupPath(input);
        var candidates = BuildPathCandidates(normalizedInput);
        if (candidates.Length == 0)
        {
            return null;
        }

        var exactMatch = await FindByExactPathAsync(organizationId, libraryId, candidates[0], ct);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var suffixMatch = await FindByAnySuffixAsync(organizationId, libraryId, candidates, ct);
        if (suffixMatch is not null)
        {
            return suffixMatch;
        }

        // Final fallback: exact match against a recorded previous path (alias) so
        // renamed documents still resolve by an old path (D-4).
        return await FindByPreviousPathAsync(organizationId, libraryId, normalizedInput, ct);
    }

    /// <inheritdoc />
    public async Task<LibraryDocument?> GetByIdAsync(
        string organizationId,
        string libraryId,
        string documentId,
        CancellationToken ct
    ) =>
        await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_id")
            .FirstOrDefaultAsync(
                row =>
                    row.OrganizationId == organizationId
                    && row.LibraryId == libraryId
                    && row.Id == documentId,
                ct
            );

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
        string organizationId,
        string libraryId,
        string query,
        int limit,
        CancellationToken ct
    )
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // ToTsQuery is a translation-only EF function (like the WebSearchToTsQuery it replaced
        // here): it must stay inline inside the expression tree, so it is repeated rather than
        // hoisted into a local. orQueryText and the lowercased fuzzy term are ordinary values and
        // are safe to compute once. See ToOrQueryText's remarks for why raw OR-of-terms replaced
        // EF.Functions.WebSearchToTsQuery here.
        var fuzzyQuery = query.ToLowerInvariant();
        var orQueryText = ToOrQueryText(query);
        var excludeCodeReviewExcluded = searchScope.ForCodeReviewExecution;

        // The OR predicate lets Postgres BitmapOr the GIN index on search_vector with the trigram
        // GIN index on title_normalized, so both retrieval signals stay index-backed in one pass.
        // Ranking happens on the entity (before projection) because EF cannot translate an ordering
        // expression built from members of a projected row.
        var rows = await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.search")
            .Where(document =>
                document.OrganizationId == organizationId
                && document.LibraryId == libraryId
                && (
                    document.SearchVector.Matches(EF.Functions.ToTsQuery("english", orQueryText))
                    || EF.Functions.TrigramsAreSimilar(document.TitleNormalized, fuzzyQuery)
                )
            )
            // Code-review execution never surfaces excluded (operational/informational) documents.
            // `excludeCodeReviewExcluded` is captured before the expression tree so the predicate
            // parameterizes as a plain bool and the query plan stays shared across both modes.
            .Where(document => !excludeCodeReviewExcluded || !document.ExcludedFromCodeReviews)
            // Tiered combiner: a full-text hit (base 1.0) always outranks a fuzzy-only hit; the
            // full-text rank orders within that tier; the trigram score breaks ties and lifts
            // documents that match both signals. ts_rank_cd is 0 for non-full-text rows.
            .OrderByDescending(document =>
                (
                    document.SearchVector.Matches(EF.Functions.ToTsQuery("english", orQueryText))
                        ? 1.0
                        : 0.0
                )
                + document.SearchVector.RankCoverDensity(
                    SearchWeights,
                    EF.Functions.ToTsQuery("english", orQueryText),
                    SearchNormalization
                )
                + (
                    EF.Functions.TrigramsAreSimilar(document.TitleNormalized, fuzzyQuery)
                        ? EF.Functions.TrigramsSimilarity(document.TitleNormalized, fuzzyQuery)
                        : 0.0
                )
            )
            .ThenBy(document => document.Path)
            .Take(limit)
            .Select(document => new
            {
                Document = document,
                MatchesFullText = document.SearchVector.Matches(
                    EF.Functions.ToTsQuery("english", orQueryText)
                ),
                MatchesFuzzy = EF.Functions.TrigramsAreSimilar(
                    document.TitleNormalized,
                    fuzzyQuery
                ),
                FullTextScore = document.SearchVector.RankCoverDensity(
                    SearchWeights,
                    EF.Functions.ToTsQuery("english", orQueryText),
                    SearchNormalization
                ),
                FuzzyScore = EF.Functions.TrigramsSimilarity(document.TitleNormalized, fuzzyQuery),
            })
            .ToArrayAsync(ct);

        return Array.ConvertAll(
            rows,
            row => new LibraryDocumentMatch(
                row.Document,
                (row.MatchesFullText, row.MatchesFuzzy) switch
                {
                    (true, true) => DocumentMatchType.Both,
                    (true, false) => DocumentMatchType.FullText,
                    _ => DocumentMatchType.Fuzzy,
                },
                row.MatchesFullText ? row.FullTextScore : 0d,
                row.MatchesFuzzy ? row.FuzzyScore : 0d
            )
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    )
    {
        // Captured before the expression tree (same reasoning as SearchAsync): parameterizes as a
        // plain bool so both modes share one query plan.
        var excludeCodeReviewExcluded = searchScope.ForCodeReviewExecution;

        return await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.list")
            .Where(document =>
                document.OrganizationId == organizationId && document.LibraryId == libraryId
            )
            // Code-review execution never lists excluded (operational/informational) documents.
            .Where(document => !excludeCodeReviewExcluded || !document.ExcludedFromCodeReviews)
            .OrderBy(document => document.Path)
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public async Task<LibraryDocument?> SetCodeReviewExclusionAsync(
        string organizationId,
        string libraryId,
        string documentId,
        bool excluded,
        CancellationToken ct
    )
    {
        // The API resolves the loaded document by stable id before calling this mutation.
        // Keep the update keyed on that identity instead of re-running path/suffix/alias
        // resolution; the toggle applies to the specific document currently open in the editor.
        var document = await GetByIdAsync(organizationId, libraryId, documentId, ct);
        if (document is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.set_code_review_exclusion"
            )
            .Where(row =>
                row.OrganizationId == document.OrganizationId
                && row.LibraryId == document.LibraryId
                && row.Id == document.Id
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.ExcludedFromCodeReviews, excluded)
                        .SetProperty(row => row.UpdatedAt, now),
                ct
            );

        document.ExcludedFromCodeReviews = excluded;
        document.UpdatedAt = now;

        return document;
    }

    /// <inheritdoc />
    public async Task<LibraryDocument?> MoveDocumentAsync(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath,
        CancellationToken ct
    )
    {
        var normalizedTo = DocumentNormalizer.NormalizePath(toPath);

        // Resolve the source via the same tiered walk used everywhere else.
        var doc = await GetByPathAsync(organizationId, libraryId, fromPath, ct);
        if (doc is null)
        {
            return null;
        }

        // NOTE: Idempotent no-op guard. `doc` is the *current* document regardless of whether the
        // caller passed its live path or an old alias, so comparing the resolved live path to the
        // target also short-circuits "rename an old alias back onto the live path" — the case a
        // reviewer flagged as a potential duplicate-alias append. Because we return here, the append
        // below never runs for that scenario, so PreviousPaths cannot accumulate the live path.
        if (doc.Path == normalizedTo)
        {
            return doc;
        }

        // Target must be free: not a live path and not an existing alias of *another* doc (D-4).
        // d.Id != doc.Id allows the current document to move back to one of its own previous aliases.
        var collides = await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.move_collision_check")
            .AnyAsync(
                d =>
                    d.OrganizationId == organizationId
                    && d.LibraryId == libraryId
                    && d.Id != doc.Id
                    && (d.Path == normalizedTo || d.PreviousPaths.Contains(normalizedTo)),
                ct
            );

        if (collides)
        {
            throw new DuplicateDocumentPathException(normalizedTo);
        }

        doc.RenameTo(normalizedTo);
        await db.SaveChangesAsync(ct);

        return doc;
    }

    private Task<LibraryDocument?> FindByExactPathAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) =>
        db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_exact_path")
            .SingleOrDefaultAsync(
                document =>
                    document.OrganizationId == organizationId
                    && document.LibraryId == libraryId
                    && document.Path == path,
                ct
            );

    /// <summary>
    /// Finds the best suffix match across all candidates in a single round trip.
    /// </summary>
    /// <remarks>
    /// Candidates are ordered most-specific-first by <see cref="BuildPathCandidates"/>.
    /// <c>UNNEST … WITH ORDINALITY</c> preserves that order as <c>c.idx</c>, so
    /// <c>ORDER BY c.idx, d.path</c> returns the most-specific match first; path is the
    /// deterministic tie-break when multiple documents match the same candidate.
    ///
    /// Raw SQL is required here because EF Core LINQ cannot express an
    /// <c>UNNEST … WITH ORDINALITY</c> join, which is the only way to fan out and
    /// rank multiple LIKE patterns against the reversed-path index in one query.
    /// </remarks>
    private Task<LibraryDocument?> FindByAnySuffixAsync(
        string organizationId,
        string libraryId,
        string[] candidates,
        CancellationToken ct
    )
    {
        var reversedPatterns = Array.ConvertAll(candidates, static c => Reverse(c) + "%");

        return db
            .LibraryDocuments.FromSql(
                $"""
                SELECT d.*
                FROM zeeq.docs_library_documents d
                JOIN UNNEST({reversedPatterns}::text[]) WITH ORDINALITY AS c(pattern, idx)
                  ON d.path_reversed LIKE c.pattern
                WHERE d.organization_id = {organizationId}
                  AND d.library_id = {libraryId}
                ORDER BY c.idx, d.path
                """
            )
            .AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_suffix_path_batched")
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

    private static string NormalizeLookupPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/').ToLowerInvariant();
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        normalized = "/" + string.Join('/', parts);

        return normalized.EndsWith(".md", StringComparison.Ordinal)
            ? normalized
            : normalized + ".md";
    }

    /// <summary>
    /// Builds an OR-of-terms raw <c>tsquery</c> string by splitting on whitespace and joining with
    /// the <c>|</c> (OR) operator, for use with <c>EF.Functions.ToTsQuery(config, query)</c>
    /// (translates to Postgres's <c>to_tsquery</c>, server-side).
    /// </summary>
    /// <remarks>
    /// Deliberately simpler than <c>websearch_to_tsquery</c>: every space-separated term is an
    /// independent OR alternative, so adding more terms broadens results instead of narrowing them.
    /// This intentionally has no operator syntax — a caller-typed quote, <c>-</c>, or the word
    /// <c>or</c> is just another literal term to OR in, not a phrase/exclude/or operator (matches
    /// the v1 <c>PostgresStorageProvider.GetDocumentByKeywordsAsync</c> precedent exactly).
    /// <c>to_tsquery</c> does not sanitize caller input the way <c>websearch_to_tsquery</c> does, so
    /// a term containing <c>tsquery</c>-special characters (<c>&amp;</c>, <c>|</c>, <c>!</c>,
    /// parentheses, <c>&lt;-&gt;</c>) can throw or change meaning — the same trade-off v1 accepted
    /// for this simpler default. <see cref="NpgsqlTypes.NpgsqlTsQuery.Parse(string)"/> was
    /// considered instead (client-side parsing, parameterized as an ordinary value) but is marked
    /// obsolete by Npgsql itself as unreliable versus the server-side grammar, so the query text is
    /// built here and evaluated by Postgres's own <c>to_tsquery</c> instead.
    /// </remarks>
    private static string ToOrQueryText(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(" | ", terms);
    }

    private static string Reverse(string value) => new(value.Reverse().ToArray());

    /// <summary>
    /// Finds a document by an exact match against a recorded previous path (alias).
    /// This is the final fallback in <see cref="GetByPathAsync"/> after the exact-path
    /// and suffix-path tiers both miss, so renamed documents still resolve by old paths (D-4).
    /// </summary>
    private Task<LibraryDocument?> FindByPreviousPathAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) =>
        db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.get_by_previous_path")
            .Where(d =>
                d.OrganizationId == organizationId
                && d.LibraryId == libraryId
                && d.PreviousPaths.Contains(path)
            )
            .FirstOrDefaultAsync(ct);
}
