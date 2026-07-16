using Zeeq.Core.Models;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Zeeq.Platform.Telemetry.Filtering;

/// <summary>
/// Extracts searchable side-column metadata from a pruned OTLP request without
/// requiring a later reparse.
/// </summary>
public sealed class TelemetryRawLogMetadataExtractor
{
    /// <summary>
    /// Extracts metadata from a pruned OTLP logs request.
    /// </summary>
    public TelemetryRawRequestMetadata ExtractLogsMetadata(
        ExportLogsServiceRequest request,
        string ingestUserId,
        int? prunedCount = null
    )
    {
        var recordCount =
            prunedCount
            ?? request
                .ResourceLogs.SelectMany(rl => rl.ScopeLogs)
                .SelectMany(sl => sl.LogRecords)
                .Count();

        var harness = DeriveHarness(request.ResourceLogs.FirstOrDefault()?.Resource);

        return new(
            Source: "otlp_http_logs",
            Harness: harness,
            RecordCount: recordCount,
            SignalType: TelemetrySignalType.Logs,
            IngestUserId: ingestUserId
        );
    }

    /// <summary>
    /// Extracts metadata from a pruned OTLP traces request.
    /// </summary>
    public TelemetryRawRequestMetadata ExtractTracesMetadata(
        ExportTraceServiceRequest request,
        string ingestUserId,
        int? prunedCount = null
    )
    {
        var recordCount =
            prunedCount
            ?? request
                .ResourceSpans.SelectMany(rs => rs.ScopeSpans)
                .SelectMany(ss => ss.Spans)
                .Count();

        var harness = DeriveHarness(request.ResourceSpans.FirstOrDefault()?.Resource);

        return new(
            Source: "otlp_http_traces",
            Harness: harness,
            RecordCount: recordCount,
            SignalType: TelemetrySignalType.Traces,
            IngestUserId: ingestUserId
        );
    }

    /// <summary>
    /// Derives the harness identity from the resource <c>service.name</c> attribute.
    /// Unknown harnesses are preserved for future extensibility.
    /// </summary>
    private static string DeriveHarness(Resource? resource)
    {
        var serviceName = resource
            ?.Attributes.FirstOrDefault(a => a.Key == "service.name")
            ?.Value?.StringValue;

        return serviceName ?? "unknown";
    }
}
