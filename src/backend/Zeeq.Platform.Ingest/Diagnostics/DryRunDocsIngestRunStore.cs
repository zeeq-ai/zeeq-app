using Zeeq.Core.Documents;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest.Diagnostics;

/// <summary>
/// In-memory, logging-only <see cref="IDocsIngestRunStore"/> for dry runs. See
/// <see cref="DryRunDocsPublicDocumentStore"/>'s remarks for the overall design.
/// </summary>
/// <remarks>
/// Unlike the document stores, this one cannot read through to the real store
/// at all — a dry run must never create a real <c>DocsIngestRun</c> row (that
/// would defeat the entire purpose: a run record referencing a <c>RunId</c>
/// that was never actually applied). It holds the run purely in memory for the
/// duration of the dry run so <see cref="RepositoryIngestRunner.RunAsync"/>'s
/// final <see cref="GetAsync"/> call — which throws if it returns
/// <see langword="null"/> — succeeds.
/// </remarks>
internal sealed partial class DryRunDocsIngestRunStore : IDocsIngestRunStore
{
    private readonly ILogger<DryRunDocsIngestRunStore> _logger;
    private DocsIngestRun? _run;

    public DryRunDocsIngestRunStore(ILogger<DryRunDocsIngestRunStore> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<DocsIngestRun> CreateAsync(DocsIngestRun run, CancellationToken ct)
    {
        _run = run;
        LogWouldCreateRun(run.Id, run.SourceKind, run.RepoUrl);
        return Task.FromResult(run);
    }

    /// <inheritdoc />
    public Task<DocsIngestRun?> GetAsync(
        string id,
        DateTimeOffset createdAtUtc,
        CancellationToken ct
    ) => Task.FromResult(_run is not null && _run.Id == id ? _run : null);

    /// <inheritdoc />
    public Task FinalizeAsync(
        string id,
        DateTimeOffset createdAtUtc,
        IngestRunFinalization finalization,
        CancellationToken ct
    )
    {
        if (_run is not null && _run.Id == id)
        {
            _run.Status = finalization.Status;
            _run.FilesTotal = finalization.FilesTotal;
            _run.FilesAdded = finalization.FilesAdded;
            _run.FilesUpdated = finalization.FilesUpdated;
            _run.FilesMoved = finalization.FilesMoved;
            _run.FilesSkipped = finalization.FilesSkipped;
            _run.FilesDeleted = finalization.FilesDeleted;
            _run.FilesFailed = finalization.FilesFailed;
            _run.AuthFailure = finalization.AuthFailure;
            _run.FailureMessage = finalization.FailureMessage;
            _run.CompletedAtUtc = finalization.CompletedAtUtc;
        }

        LogWouldFinalizeRun(
            id,
            finalization.Status,
            finalization.FilesTotal,
            finalization.FilesAdded,
            finalization.FilesUpdated,
            finalization.FilesMoved,
            finalization.FilesDeleted,
            finalization.FilesFailed
        );

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocsIngestRun>> ListByOrganizationAsync(
        string organizationId,
        int limit,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<DocsIngestRun>> ListByLibraryAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<DocsIngestRun>> ListByPublicSourceAsync(
        string publicSourceId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would create ingest run. RunId={RunId}, SourceKind={SourceKind}, RepoUrl={RepoUrl}"
    )]
    private partial void LogWouldCreateRun(
        string runId,
        RepositorySourceKind sourceKind,
        string repoUrl
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would finalize ingest run. RunId={RunId}, Status={Status}, Total={FilesTotal}, Added={FilesAdded}, Updated={FilesUpdated}, Moved={FilesMoved}, Deleted={FilesDeleted}, Failed={FilesFailed}"
    )]
    private partial void LogWouldFinalizeRun(
        string runId,
        IngestRunStatus status,
        int filesTotal,
        int filesAdded,
        int filesUpdated,
        int filesMoved,
        int filesDeleted,
        int filesFailed
    );
}
