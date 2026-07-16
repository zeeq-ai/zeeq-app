namespace Zeeq.Core.Models;

/// <summary>
/// Transient raw storage row for one inbound OTLP export request (logs or traces).
/// </summary>
/// <remarks>
/// The payload is a serialized <c>ExportLogsServiceRequest</c> or
/// <c>ExportTraceServiceRequest</c> protobuf. Domain rows must not reference this
/// transient table — successful processing deletes the raw row. The backing table
/// is <c>UNLOGGED</c> so rows are lost on database restart (acceptable for
/// best-effort telemetry).
/// </remarks>
public sealed class TelemetryRawRequest
{
    /// <summary>UUID v7 row identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Server time when the request was accepted.</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary><c>Logs</c> or <c>Traces</c>.</summary>
    public TelemetrySignalType SignalType { get; init; }

    /// <summary>Authenticated identity from the validated JWT.</summary>
    public string? IngestUserId { get; set; }

    /// <summary>Organization from the validated JWT principal.</summary>
    public string? IngestOrganizationId { get; set; }

    /// <summary>Source descriptor (e.g. <c>otlp_http_logs</c>).</summary>
    public string? SourceName { get; init; }

    /// <summary>Harness hint derived from stable source metadata.</summary>
    public string? HarnessName { get; init; }

    /// <summary>Total number of OTLP records (log records or spans) in the raw request.</summary>
    public required int RecordCount { get; init; }

    /// <summary>Serialized protobuf bytes.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Current processing state for worker leasing.</summary>
    public TelemetryRawRequestProcessingStatus ProcessingStatus { get; set; } =
        TelemetryRawRequestProcessingStatus.Pending;

    /// <summary>Worker lease identifier.</summary>
    public string? ProcessingLeaseId { get; set; }

    /// <summary>Time when the current worker lease expires.</summary>
    public DateTimeOffset? ProcessingLeaseExpiresAtUtc { get; set; }

    /// <summary>Obfuscation policy version applied at ingest time.</summary>
    public int EffectiveObfuscationPolicyVersion { get; set; }

    /// <summary>Number of processing attempts so far.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Quarantine reason when the payload is terminally malformed.</summary>
    public string? QuarantineReason { get; set; }

    /// <summary>When the row was quarantined.</summary>
    public DateTimeOffset? QuarantinedAtUtc { get; set; }
}

/// <summary>Processing state for transient raw telemetry rows.</summary>
public enum TelemetryRawRequestProcessingStatus
{
    /// <summary>The request is ready for a processing worker to claim.</summary>
    Pending,

    /// <summary>The request has been leased by a processing worker.</summary>
    Processing,

    /// <summary>The request cannot be processed and has been quarantined.</summary>
    Quarantined,
}

/// <summary>Side-column metadata extracted from a raw telemetry request.</summary>
/// <param name="Source">Source descriptor (e.g. <c>otlp_http_logs</c>).</param>
/// <param name="Harness">Harness hint from <c>service.name</c>.</param>
/// <param name="RecordCount">Number of OTLP records in the request.</param>
/// <param name="SignalType">Logs or Traces.</param>
/// <param name="IngestUserId">Authenticated user identity.</param>
public sealed record TelemetryRawRequestMetadata(
    string Source,
    string Harness,
    int RecordCount,
    TelemetrySignalType SignalType,
    string IngestUserId
);
