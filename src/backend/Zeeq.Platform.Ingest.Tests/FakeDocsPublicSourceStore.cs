using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// In-memory <see cref="IDocsPublicSourceStore"/> covering just what the
/// manual-trigger handler tests need — lookup and update. See
/// <see cref="FakeLibraryDocumentStore"/> for why the rest throws.
/// </summary>
internal sealed class FakeDocsPublicSourceStore : IDocsPublicSourceStore
{
    public List<DocsPublicSource> Sources { get; } = [];

    public Task<DocsPublicSource?> GetByIdAsync(string id, CancellationToken ct) =>
        Task.FromResult(Sources.SingleOrDefault(s => s.Id == id));

    public Task<DocsPublicSource> UpdateAsync(DocsPublicSource source, CancellationToken ct)
    {
        var existing = Sources.Single(s => s.Id == source.Id);
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

        return Task.FromResult(existing);
    }

    public Task<DocsPublicSource?> GetByRepoUrlAsync(string repoUrl, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DocsPublicSource>> GetByIdsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken ct
    ) => Task.FromResult<IReadOnlyList<DocsPublicSource>>(Sources.Where(s => ids.Contains(s.Id)).ToArray());

    public Task<DocsPublicSource> CreateAsync(DocsPublicSource source, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DocsPublicSource>> ClaimDueForSyncAsync(
        int limit,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<bool> TryUpdateCurrentSyncLeaseAsync(
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
        var source = Sources.Single(s => s.Id == sourceId);
        if (
            source.ActiveSyncRunId != expectedRunId
            || source.ActiveSyncRunCreatedAtUtc != expectedRunCreatedAtUtc
        )
        {
            return Task.FromResult(false);
        }

        source.SyncStatus = syncStatus;
        source.NextSyncAt = nextSyncAt;
        source.SyncedAt = syncedAt;
        source.Status = status;
        source.ActiveSyncRunId = activeSyncRunId;
        source.ActiveSyncRunCreatedAtUtc = activeSyncRunCreatedAtUtc;
        source.SyncQueuedAtUtc = syncQueuedAtUtc;
        source.SyncStartedAtUtc = syncStartedAtUtc;
        source.UpdatedAt = updatedAt;

        return Task.FromResult(true);
    }
}
