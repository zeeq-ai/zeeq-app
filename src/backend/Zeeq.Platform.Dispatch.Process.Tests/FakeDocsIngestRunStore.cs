using Zeeq.Core.Documents;

namespace Zeeq.Platform.Dispatch.Process.Tests;

/// <summary>
/// Minimal in-memory <see cref="IDocsIngestRunStore"/> for dispatcher tests.
/// See <see cref="FakeDocsPublicDocumentStore"/> for why this is a local
/// duplicate rather than shared across test projects.
/// </summary>
internal sealed class FakeDocsIngestRunStore : IDocsIngestRunStore
{
    private readonly Dictionary<(string Id, DateTimeOffset CreatedAtUtc), DocsIngestRun> _runs = [];

    public Task<DocsIngestRun> CreateAsync(DocsIngestRun run, CancellationToken ct)
    {
        var key = (run.Id, run.CreatedAtUtc);

        if (_runs.ContainsKey(key))
        {
            throw new InvalidOperationException($"Run {run.Id} already exists.");
        }

        _runs[key] = run;
        return Task.FromResult(run);
    }

    public Task<DocsIngestRun?> GetAsync(
        string id,
        DateTimeOffset createdAtUtc,
        CancellationToken ct
    ) => Task.FromResult(_runs.GetValueOrDefault((id, createdAtUtc)));

    public Task FinalizeAsync(
        string id,
        DateTimeOffset createdAtUtc,
        IngestRunFinalization finalization,
        CancellationToken ct
    )
    {
        var run = _runs[(id, createdAtUtc)];
        run.Status = finalization.Status;
        run.FilesTotal = finalization.FilesTotal;
        run.FilesAdded = finalization.FilesAdded;
        run.FilesUpdated = finalization.FilesUpdated;
        run.FilesMoved = finalization.FilesMoved;
        run.FilesSkipped = finalization.FilesSkipped;
        run.FilesDeleted = finalization.FilesDeleted;
        run.FilesFailed = finalization.FilesFailed;
        run.AuthFailure = finalization.AuthFailure;
        run.FailureMessage = finalization.FailureMessage;
        run.CompletedAtUtc = finalization.CompletedAtUtc;
        run.UpdatedAtUtc = finalization.CompletedAtUtc;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocsIngestRun>> ListByOrganizationAsync(
        string organizationId,
        int limit,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<DocsIngestRun>>([
            .. _runs.Values.Where(r => r.OrganizationId == organizationId).Take(limit),
        ]);

    public Task<IReadOnlyList<DocsIngestRun>> ListByLibraryAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<DocsIngestRun>>([
            .. _runs
                .Values.Where(r => r.OrganizationId == organizationId && r.LibraryId == libraryId)
                .Take(limit),
        ]);

    public Task<IReadOnlyList<DocsIngestRun>> ListByPublicSourceAsync(
        string publicSourceId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<DocsIngestRun>>([
            .. _runs.Values.Where(r => r.PublicSourceId == publicSourceId).Take(limit),
        ]);
}
