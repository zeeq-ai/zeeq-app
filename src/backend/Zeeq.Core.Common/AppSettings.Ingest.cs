namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Repository content ingest configuration options.
    /// </summary>
    public IngestSettings Ingest { get; init; } = new();
}

/// <summary>
/// Configuration options for the repository content ingest pipeline.
/// </summary>
public sealed record IngestSettings
{
    /// <summary>
    /// The dispatch runtime used to execute ingest jobs. Must match one of the
    /// registered <c>IRepositoryIngestDispatcher</c> implementations.
    /// </summary>
    public string Runtime { get; init; } = "InProcess";

    /// <summary>
    /// Root directory for ingest workspaces (git clones). Defaults to the OS temp
    /// directory for local development; set to a mounted volume path in
    /// production (e.g. <c>/mnt/ingest</c>).
    /// </summary>
    public string? ContentRootPath { get; init; }

    /// <summary>
    /// Maximum concurrent private-repository ingest runs.
    /// </summary>
    public int MaxConcurrentPrivate { get; init; } = 4;

    /// <summary>
    /// Maximum concurrent public-repository ingest runs.
    /// </summary>
    public int MaxConcurrentPublic { get; init; } = 2;

    /// <summary>
    /// Scheduler tick period, in seconds.
    /// </summary>
    public int SchedulerPeriodSeconds { get; init; } = 300;

    /// <summary>
    /// Maximum number of due sources claimed per scheduler tick.
    /// </summary>
    public int SchedulerBatchSize { get; init; } = 10;

    /// <summary>
    /// Sync interval applied after a run completes, in seconds, before jitter.
    /// </summary>
    public int SyncIntervalSeconds { get; init; } = 3600;

    /// <summary>
    /// Jitter applied to the next sync time, as a fraction of the sync interval (e.g. 0.2 = ±20%).
    /// </summary>
    public double SyncJitterFraction { get; init; } = 0.2;

    /// <summary>
    /// Rolling window, in seconds, used for manual-trigger rate limiting.
    /// </summary>
    public int ManualTriggerWindowSeconds { get; init; } = 3600;

    /// <summary>
    /// Maximum manual triggers allowed within <see cref="ManualTriggerWindowSeconds"/>.
    /// </summary>
    public int ManualTriggerMaxInWindow { get; init; } = 5;
}
