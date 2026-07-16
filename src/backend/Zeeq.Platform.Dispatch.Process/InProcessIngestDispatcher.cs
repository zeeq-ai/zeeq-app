using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Platform.Ingest;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Dispatch.Process;

/// <summary>
/// Runs a repository ingest job inline in the current process.
/// </summary>
/// <remarks>
/// Composes the three independently-built, independently-tested pieces of
/// Phase 1 into one <see cref="IRepositoryIngestDispatcher"/>: acquires a
/// workspace (<see cref="LocalTempWorkspaceProvider"/>, real git clone-or-pull),
/// runs the ingest algorithm against it (<see cref="RepositoryIngestRunner"/>),
/// and disposes the workspace when done. This class adds no new ingest logic —
/// it is deliberately thin wiring, per spec §4.3's dispatcher/runner split.
/// <para>
/// <b>Run-record coverage for workspace-acquisition failures.</b>
/// <see cref="RepositoryIngestRunner"/> only creates a
/// <see cref="DocsIngestRun"/> record once it starts (its first statement
/// after the private-source guard). A clone/pull failure happens in
/// <see cref="LocalTempWorkspaceProvider.AcquireAsync"/>, before the runner is
/// ever invoked — without this dispatcher recording something, a bad URL,
/// network outage, or auth failure during cloning would leave <i>zero</i>
/// audit trail, directly contradicting this feature's core design pillar
/// (draft objective: "a single run failure is seen as best-effort but should
/// be visible and reportable for each repository"). <see cref="RunAsync"/>
/// therefore writes a <see cref="IngestRunStatus.Failed"/> run record itself
/// when acquisition fails, and defensively does the same if the runner throws
/// for a reason its own internal handling didn't already cover (e.g. a
/// private-source job's <see cref="NotSupportedException"/>, thrown before the
/// runner creates its own record).
/// </para>
/// </remarks>
public sealed partial class InProcessIngestDispatcher(
    IIngestWorkspaceProvider workspaceProvider,
    RepositoryIngestRunner runner,
    IDocsIngestRunStore runStore,
    ILogger<InProcessIngestDispatcher> logger
) : IRepositoryIngestDispatcher
{
    /// <inheritdoc />
    public DispatchRuntime Runtime => DispatchRuntime.InProcess;

    /// <inheritdoc />
    public async Task<DispatchOutcome> RunAsync(
        RepositoryIngestJob job,
        CancellationToken cancellationToken
    )
    {
        IIngestWorkspace workspace;

        try
        {
            workspace = await workspaceProvider.AcquireAsync(job, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogAcquireFailed(logger, job.RunId, job.RepoUrl, ex);
            await RecordFatalFailureAsync(job, ex, cancellationToken);

            return new DispatchOutcome(DispatchOutcomeKind.Failed, ex.Message);
        }

        try
        {
            var run = await runner.RunAsync(job, workspace, cancellationToken);

            // Completed covers both Succeeded and Partial — a partial run
            // still did real work and is reported via the run record's own
            // status/counts, not the dispatch outcome. Only a fatal run
            // (Failed — the runner's own enumeration-level catch already
            // finalized it before returning) maps to a Failed outcome here.
            return run.Status == IngestRunStatus.Failed
                ? new DispatchOutcome(DispatchOutcomeKind.Failed, run.FailureMessage)
                : new DispatchOutcome(DispatchOutcomeKind.Completed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRunFailed(logger, job.RunId, job.RepoUrl, ex);
            await RecordFatalFailureAsync(job, ex, cancellationToken);

            return new DispatchOutcome(DispatchOutcomeKind.Failed, ex.Message);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    /// <summary>
    /// Ensures a <see cref="IngestRunStatus.Failed"/> run record exists for
    /// <paramref name="job"/>, regardless of whether the runner had already
    /// created one.
    /// </summary>
    /// <remarks>
    /// Tries create-then-finalize first (the common case: no record exists
    /// yet, e.g. an acquisition failure). If a record already exists — the
    /// runner created one and then something else failed after — the create
    /// call fails and this falls through to finalize-only, which overwrites
    /// whatever state the existing record was left in.
    /// </remarks>
    private async Task RecordFatalFailureAsync(
        RepositoryIngestJob job,
        Exception ex,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;

        try
        {
            await runStore.CreateAsync(
                new DocsIngestRun
                {
                    Id = job.RunId,
                    CreatedAtUtc = job.RunCreatedAtUtc,
                    SourceKind = job.Kind,
                    RepoUrl = job.RepoUrl,
                    PublicSourceId = job.PublicSourceId,
                    OrganizationId = job.OrganizationId,
                    LibraryId = job.LibraryId,
                    Trigger = job.Trigger,
                    Status = IngestRunStatus.Running,
                    StartedAtUtc = now,
                    UpdatedAtUtc = now,
                },
                cancellationToken
            );
        }
        catch (Exception)
        {
            // A run record likely already exists — proceed to finalize either way.
        }

        try
        {
            await runStore.FinalizeAsync(
                job.RunId,
                job.RunCreatedAtUtc,
                new IngestRunFinalization(
                    Status: IngestRunStatus.Failed,
                    FilesTotal: 0,
                    FilesAdded: 0,
                    FilesUpdated: 0,
                    FilesMoved: 0,
                    FilesSkipped: 0,
                    FilesDeleted: 0,
                    FilesFailed: 0,
                    // Heuristic: the token provider's own fail-fast exception
                    // is InvalidOperationException; anything else (git
                    // failures, cancellation races, etc.) is not classified
                    // as an auth failure here.
                    AuthFailure: ex is InvalidOperationException,
                    FailureMessage: ex.Message,
                    CompletedAtUtc: now
                ),
                cancellationToken
            );
        }
        catch (Exception finalizeEx)
        {
            LogFailureRecordingFailed(logger, job.RunId, finalizeEx);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Ingest workspace acquisition failed. RunId={RunId}, RepoUrl={RepoUrl}"
    )]
    private static partial void LogAcquireFailed(
        ILogger logger,
        string runId,
        string repoUrl,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Ingest run threw unexpectedly. RunId={RunId}, RepoUrl={RepoUrl}"
    )]
    private static partial void LogRunFailed(
        ILogger logger,
        string runId,
        string repoUrl,
        Exception ex
    );

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Failed to record a fatal ingest failure on the run record. RunId={RunId}"
    )]
    private static partial void LogFailureRecordingFailed(
        ILogger logger,
        string runId,
        Exception ex
    );
}
