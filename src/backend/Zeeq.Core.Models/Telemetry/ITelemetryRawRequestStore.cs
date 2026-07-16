namespace Zeeq.Core.Models;

/// <summary>
/// Stores inbound OTLP telemetry export payloads (logs and traces) as raw protobuf bytes
/// before any domain parsing runs. The raw table is deliberately <c>UNLOGGED</c> in
/// Postgres — rows survive process crashes but not database restarts, which is acceptable
/// for best-effort telemetry.
/// </summary>
/// <remarks>
/// Parser and domain-processing services intentionally do not participate in the
/// request path so collector exports can return quickly.
///
/// The lease-based claim pattern ensures multiple concurrent processors can work
/// on the same raw table without stepping on each other. A crashed processor's rows
/// are reclaimed when their lease expires.
/// </remarks>
public interface ITelemetryRawRequestStore
{
    /// <summary>
    /// Persists one OTLP export request as a raw protobuf payload with lease metadata.
    /// </summary>
    /// <param name="payload">The serialized protobuf bytes.</param>
    /// <param name="signalType">Logs or Traces.</param>
    /// <param name="metadata">Side-column metadata extracted once by the receiver.</param>
    /// <param name="ingestUserId">Authenticated identity from the validated JWT.</param>
    /// <param name="ingestOrganizationId">Organization from the validated JWT.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw request row ID.</returns>
    Task<string> StoreLogsAsync(
        byte[] payload,
        TelemetrySignalType signalType,
        TelemetryRawRequestMetadata metadata,
        string ingestUserId,
        string ingestOrganizationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Persists one OTLP trace export request as a raw protobuf payload with lease metadata.
    /// </summary>
    /// <inheritdoc cref="StoreLogsAsync"/>
    Task<string> StoreTracesAsync(
        byte[] payload,
        TelemetrySignalType signalType,
        TelemetryRawRequestMetadata metadata,
        string ingestUserId,
        string ingestOrganizationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Claims pending raw requests for a processing worker.
    /// </summary>
    /// <param name="batchSize">Maximum number of rows to claim.</param>
    /// <param name="leaseDuration">How long the claim remains valid.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Claimed raw requests ordered by receive time.</returns>
    Task<IReadOnlyList<TelemetryRawRequest>> ClaimBatchAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deletes a raw request still owned by the supplied lease.
    /// </summary>
    /// <remarks>
    /// Used for both successful processing and logged processing failures. Requiring
    /// the lease ID prevents one worker from deleting a row reclaimed by another worker
    /// after the original lease expired.
    /// </remarks>
    /// <param name="rawRequestId">The raw request row ID.</param>
    /// <param name="processingLeaseId">The worker lease ID that owns the row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true" /> when a row was deleted.</returns>
    Task<bool> DeleteClaimedAsync(
        string rawRequestId,
        string processingLeaseId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Releases expired processing leases back to pending.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows released.</returns>
    Task<int> ReleaseExpiredLeasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Quarantines a malformed payload, preventing it from being claimed again.
    /// Only succeeds when the row is still owned by the supplied lease.
    /// </summary>
    /// <param name="rawRequestId">The raw request row ID.</param>
    /// <param name="processingLeaseId">The lease that must still own the row.</param>
    /// <param name="reason">Human-readable reason for quarantine.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task QuarantineAsync(
        string rawRequestId,
        string processingLeaseId,
        string reason,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Records a processing failure on a raw row, resetting it to pending.
    /// Only succeeds when the row is still owned by the supplied lease.
    /// </summary>
    /// <param name="rawRequestId">The raw request row ID.</param>
    /// <param name="processingLeaseId">The lease that must still own the row.</param>
    /// <param name="error">The exception that caused the failure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordProcessingFailureAsync(
        string rawRequestId,
        string processingLeaseId,
        Exception error,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Discriminates between log and trace signal types in raw storage.</summary>
public enum TelemetrySignalType
{
    /// <summary>OTLP log signal (<c>ExportLogsServiceRequest</c>).</summary>
    Logs,

    /// <summary>OTLP trace signal (<c>ExportTraceServiceRequest</c>).</summary>
    Traces,
}
