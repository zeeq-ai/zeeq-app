using OtlpLog = OpenTelemetry.Proto.Logs.V1.LogRecord;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Zeeq.Platform.Telemetry.Adapters;

/// <summary>
/// Harness-specific log record filter. Implementations decide whether a raw OTLP
/// log record should be kept for processing, given the <c>service.name</c> from
/// the resource.
/// </summary>
/// <remarks>
/// This is the fast-path defensive backstop — it operates on raw protobuf records
/// before the more expensive attribute-map construction in
/// <see cref="TelemetryLogRecordContext"/>. Adapters that handle log signals
/// implement this interface with the same harness-specific knowledge used in
/// <see cref="IAgentTelemetryAdapter.CanHandle(TelemetryLogRecordContext)"/>.
/// </remarks>
public interface ITelemetryLogFilter
{
    /// <summary>
    /// Determines whether this filter owns records from the supplied service.
    /// </summary>
    /// <param name="serviceName">The <c>service.name</c> from the resource.</param>
    /// <returns>True when the service belongs to this harness.</returns>
    bool HandlesService(string serviceName);

    /// <summary>
    /// Determines whether a raw OTLP log record should be kept.
    /// </summary>
    /// <param name="record">The raw log record.</param>
    /// <param name="serviceName">The <c>service.name</c> from the resource.</param>
    /// <returns>True if the record should be kept.</returns>
    bool ShouldKeepLogRecord(OtlpLog record, string serviceName);
}

/// <summary>
/// Harness-specific span filter. Implementations decide whether a raw OTLP
/// span should be kept for processing, given the <c>service.name</c> from
/// the resource.
/// </summary>
public interface ITelemetrySpanFilter
{
    /// <summary>
    /// Determines whether a raw OTLP span should be kept.
    /// </summary>
    /// <param name="span">The raw span.</param>
    /// <param name="serviceName">The <c>service.name</c> from the resource.</param>
    /// <returns>True if the span should be kept.</returns>
    bool ShouldKeepSpan(OtlpSpan span, string serviceName);
}
