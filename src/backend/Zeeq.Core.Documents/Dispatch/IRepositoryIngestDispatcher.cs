namespace Zeeq.Core.Documents.Dispatch;

/// <summary>
/// Runtime-selectable executor for a <see cref="RepositoryIngestJob"/>. One
/// implementation per target runtime (in-process, isolated process, Cloud Run
/// Job, ...).
/// </summary>
/// <remarks>
/// The dispatcher owns runtime concerns only — where the work runs and how the
/// outcome is reported back. All ingest business logic lives in
/// `RepositoryIngestRunner` so it stays testable without any runtime
/// infrastructure. Adding a new runtime is one new class implementing this
/// interface; nothing else in the pipeline changes.
/// </remarks>
public interface IRepositoryIngestDispatcher
{
    /// <summary>Stable key used to select this dispatcher from <c>AppSettings.Ingest.Runtime</c>.</summary>
    DispatchRuntime Runtime { get; }

    /// <summary>Executes the job on this dispatcher's runtime and returns its outcome.</summary>
    Task<DispatchOutcome> RunAsync(RepositoryIngestJob job, CancellationToken cancellationToken);
}

/// <summary>
/// Runtimes a <see cref="RepositoryIngestJob"/> can be dispatched to.
/// </summary>
/// <remarks>
/// Values beyond <see cref="InProcess"/> and <see cref="IsolatedProcess"/> are
/// reserved so configuration and telemetry are forward-compatible even before
/// the corresponding dispatcher ships.
/// </remarks>
public enum DispatchRuntime
{
    /// <summary>Runs the runner inline in the current process. v1 default.</summary>
    InProcess,

    /// <summary>Spawns the same container binary with <c>ZEEQ_RUN_MODE=ingest-job</c>.</summary>
    IsolatedProcess,

    /// <summary>Triggers a GCP Cloud Run Job execution.</summary>
    GcpCloudRunJob,

    /// <summary>Reserved for a future Azure Container Instance dispatcher.</summary>
    AzureContainerInstance,

    /// <summary>Reserved for a future Kubernetes Job dispatcher.</summary>
    K8sJob,
}

/// <summary>Terminal outcome kind for a dispatched job.</summary>
public enum DispatchOutcomeKind
{
    /// <summary>The job ran to completion (regardless of per-file failures — see the run record status).</summary>
    Completed,

    /// <summary>The job could not complete due to a fatal error (e.g. clone/auth failure).</summary>
    Failed,

    /// <summary>The dispatcher declined to run the job (e.g. a quarantined public source).</summary>
    Rejected,
}

/// <summary>Result reported by a dispatcher after attempting a job.</summary>
/// <param name="Kind">The terminal outcome.</param>
/// <param name="FailureReason">Human-readable reason, set for <see cref="DispatchOutcomeKind.Failed"/> or <see cref="DispatchOutcomeKind.Rejected"/>.</param>
public sealed record DispatchOutcome(DispatchOutcomeKind Kind, string? FailureReason = null);
