using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// In-memory <see cref="IDocsIngestRunStore"/> keyed the same way the
/// partitioned Postgres table is — by (id, created_at_utc) — so runner tests
/// exercise the same lookup shape without a database.
/// </summary>
internal sealed class FakeDocsIngestRunStore : IDocsIngestRunStore
{
    private readonly Dictionary<(string Id, DateTimeOffset CreatedAtUtc), DocsIngestRun> _runs = [];

    public Task<DocsIngestRun> CreateAsync(DocsIngestRun run, CancellationToken ct)
    {
        _runs[(run.Id, run.CreatedAtUtc)] = run;
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

    // Cursor param unused here: this fake backs RepositoryIngestRunner tests
    // only, which never call the list-with-pagination methods — see the
    // dedicated ListByLibraryAsync/ListByPublicSourceAsync tests against the
    // real Postgres store for pagination coverage.
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
