namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Agent session telemetry ingest and processing configuration.
    /// </summary>
    public TelemetrySettings Telemetry { get; init; } = new();
}

/// <summary>
/// Settings for the agent telemetry ingest pipeline and processing service.
/// </summary>
public sealed record TelemetrySettings
{
    /// <summary>
    /// Maximum number of raw telemetry rows to claim in one processing batch.
    /// </summary>
    public int IngestBatchSize { get; init; } = 100;

    /// <summary>
    /// How long a processing lease remains valid (in seconds) before another
    /// processor instance can reclaim unacknowledged rows.
    /// </summary>
    public int LeaseTtlSeconds { get; init; } = 60;

    /// <summary>
    /// Poll interval (in milliseconds) between processing cycles when no raw
    /// rows are available.
    /// </summary>
    public int ProcessingPollIntervalMs { get; init; } = 5000;
}
