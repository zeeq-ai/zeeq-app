namespace Zeeq.Core.Documents;

/// <summary>
/// Store contract for globally-shared public repository sources.
/// </summary>
public interface IDocsPublicSourceStore
{
    /// <summary>Gets a source by id.</summary>
    Task<DocsPublicSource?> GetByIdAsync(string id, CancellationToken ct);

    /// <summary>
    /// Batch-gets sources by id, for mapping a list of libraries to their
    /// backing public sources in one query instead of one per library.
    /// Unknown ids are simply omitted from the result — not an error.
    /// </summary>
    Task<IReadOnlyList<DocsPublicSource>> GetByIdsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken ct
    );

    /// <summary>Gets a source by its canonical, globally-unique repository URL.</summary>
    Task<DocsPublicSource?> GetByRepoUrlAsync(string repoUrl, CancellationToken ct);

    /// <summary>Creates a new public source.</summary>
    Task<DocsPublicSource> CreateAsync(DocsPublicSource source, CancellationToken ct);

    /// <summary>
    /// Updates the mutable fields of an existing source (name, default filters,
    /// sync lifecycle, status, manual trigger history).
    /// </summary>
    Task<DocsPublicSource> UpdateAsync(DocsPublicSource source, CancellationToken ct);

    /// <summary>
    /// Atomically claims up to <paramref name="limit"/> idle, active sources whose
    /// <c>next_sync_at</c> has passed, transitioning them to <c>queued</c> and
    /// returning the claimed rows. Uses <c>FOR UPDATE SKIP LOCKED</c> so
    /// concurrent scheduler ticks (or worker replicas) never double-claim the
    /// same source.
    /// </summary>
    Task<IReadOnlyList<DocsPublicSource>> ClaimDueForSyncAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Updates sync fields only if the source is still owned by the expected active run.
    /// </summary>
    Task<bool> TryUpdateCurrentSyncLeaseAsync(
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
        throw new NotSupportedException();
    }

    /// <summary>
    /// Clears stale queued/running public-source syncs.
    /// </summary>
    Task<IReadOnlyList<StalledSyncReset>> ResetStalledSyncsAsync(
        DateTimeOffset now,
        TimeSpan queuedStaleAfter,
        TimeSpan runningStaleAfter,
        int limit,
        CancellationToken ct
    )
    {
        throw new NotSupportedException();
    }
}
