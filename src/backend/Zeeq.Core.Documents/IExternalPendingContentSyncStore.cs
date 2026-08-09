namespace Zeeq.Core.Documents;

/// <summary>
/// Store for the dirty-content set backing incremental externally-sourced ingest runs
/// (e.g. Notion, and future providers such as SharePoint or OneDrive).
/// </summary>
/// <remarks>
/// Backed by <c>docs_external_pending_content_syncs</c>, keyed <c>(organization_id, library_id,
/// external_content_id)</c> — a set, not an event log. Webhook deliveries upsert into this set
/// (coalescing repeated edits to the same item into one pending row); an incremental sync run
/// claims and then clears rows it successfully processes.
/// </remarks>
public interface IExternalPendingContentSyncStore
{
    /// <summary>
    /// Marks an item dirty, or bumps an already-dirty item's <c>event_type</c>/timestamp.
    /// </summary>
    /// <remarks>
    /// Resets any existing claim on the row. This is the correctness pivot for the mid-run
    /// re-dirty race: if a run has already claimed this item and is mid-fetch, resetting the
    /// claim here means that run's later <see cref="ClearAsync"/> call will no-op (the
    /// <c>claimed_by_run_id</c> guard no longer matches), so the edit is not silently lost —
    /// it survives for the next run.
    /// </remarks>
    Task UpsertAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        string eventType,
        DateTimeOffset markedDirtyAtUtc,
        CancellationToken ct
    );

    /// <summary>
    /// Atomically claims every unclaimed (or stale-claimed) pending row for one library, using
    /// <c>FOR UPDATE SKIP LOCKED</c> so concurrent runs can never double-claim.
    /// </summary>
    /// <remarks>
    /// A row also becomes claimable again once its existing claim is older than
    /// <paramref name="staleClaimAfter"/> — the reclaim path for a run that crashed or was
    /// killed after claiming but before calling <see cref="ClearAsync"/>. Without this, a
    /// claim with no matching webhook re-dirty would be stranded forever, since the only other
    /// path back to "claimable" is <see cref="UpsertAsync"/> resetting it. This mirrors
    /// <c>ILibraryDocumentStore.ResetStalledSyncsAsync</c>'s time-based staleness recovery for
    /// library-level sync leases.
    /// </remarks>
    Task<IReadOnlyList<ExternalPendingContentSync>> ClaimAsync(
        string organizationId,
        string libraryId,
        string runId,
        DateTimeOffset claimedAtUtc,
        TimeSpan staleClaimAfter,
        CancellationToken ct
    );

    /// <summary>
    /// Compare-and-delete: removes the pending row only if it is still claimed by
    /// <paramref name="runId"/>. No-ops (does not delete) if the row was re-dirtied
    /// (and therefore un-claimed) after this run's claim.
    /// </summary>
    Task ClearAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        string runId,
        CancellationToken ct
    );

    /// <summary>
    /// Removes a pending row regardless of claim ownership after the upstream item is
    /// authoritatively absent.
    /// </summary>
    Task RemoveAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        CancellationToken ct
    );

    /// <summary>
    /// Reports whether <paramref name="runId"/> still owns an active claim on this row.
    /// </summary>
    /// <remarks>
    /// The incremental runner calls this immediately before writing a fetched page, to catch a
    /// concurrent <see cref="RemoveAsync"/> from a <c>page.deleted</c> webhook: that call deletes
    /// the row outright, so a claim this run took earlier no longer exists and this returns
    /// <see langword="false"/> — telling the runner to drop its in-flight write instead of
    /// resurrecting a page Notion says was deleted. This narrows, but (being a separate read from
    /// the eventual write) does not fully close, the race window.
    /// </remarks>
    Task<bool> IsClaimActiveAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        string runId,
        CancellationToken ct
    );
}

/// <summary>One claimed pending-content-sync row.</summary>
public sealed record ExternalPendingContentSync(string ExternalContentId, string EventType);
