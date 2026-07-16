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
}
