using Google.Protobuf;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Filtering;

namespace Zeeq.Platform.Telemetry.Ingest.Otlp;

/// <summary>
/// Shared per-request flow: prune via adapter chain → extract side-column metadata →
/// store raw protobuf. Shared by both HTTP receivers and the REST import path.
/// </summary>
/// <remarks>
/// Creates the ingest service with required filter and store dependencies.
/// </remarks>
public sealed class OtlpLogIngestService(
    ITelemetryRawRequestStore rawStore,
    AgentTelemetryLogFilter logFilter,
    AgentTelemetrySpanFilter spanFilter,
    TelemetryRawLogMetadataExtractor metadataExtractor,
    ILogger<OtlpLogIngestService> log
)
{
    private const string UserEmailAttribute = "user.email";

    /// <summary>
    /// Stores a pruned OTLP logs request into the raw store.
    /// </summary>
    /// <returns>The count of accepted log records, or 0 if none match.</returns>
    public async Task<int> StoreLogsAsync(
        byte[] payload,
        string ingestUserId,
        string ingestOrganizationId,
        string? ingestUserEmail = null,
        CancellationToken cancellationToken = default
    )
    {
        var request = ExportLogsServiceRequest.Parser.ParseFrom(payload);
        var pruned = logFilter.PruneAcceptedLogsInPlace(request);

        if (pruned == 0)
        {
            return 0;
        }

        ApplyAuthenticatedUserEmail(request, ingestUserEmail);

        var metadata = metadataExtractor.ExtractLogsMetadata(request, ingestUserId, pruned);
        // NOTE: Keep this bounded receive span on the ingest path for rollout diagnostics.
        // Central OpenTelemetry sampling controls its cost; tags exclude payload and identity values.
        using var activity = ZeeqTelemetry.Tracer.StartActivity("telemetry.ingest.logs");
        activity?.SetTag("telemetry.signal_type", "logs");
        activity?.SetTag("telemetry.record_count", pruned);
        activity?.SetTag("telemetry.harness", metadata.Harness);

        await rawStore.StoreLogsAsync(
            request.ToByteArray(),
            TelemetrySignalType.Logs,
            metadata,
            ingestUserId,
            ingestOrganizationId,
            cancellationToken
        );

        log.LogDebug(
            "Stored {RecordCount} log records from {Harness} via {IngestUserId}",
            pruned,
            metadata.Harness,
            ingestUserId
        );

        return pruned;
    }

    private static void ApplyAuthenticatedUserEmail(
        ExportLogsServiceRequest request,
        string? ingestUserEmail
    )
    {
        foreach (var resourceLogs in request.ResourceLogs)
        {
            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                foreach (var record in scopeLogs.LogRecords)
                {
                    for (var i = record.Attributes.Count - 1; i >= 0; i--)
                    {
                        if (record.Attributes[i].Key == UserEmailAttribute)
                        {
                            record.Attributes.RemoveAt(i);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(ingestUserEmail))
                    {
                        record.Attributes.Add(
                            new KeyValue
                            {
                                Key = UserEmailAttribute,
                                Value = new() { StringValue = ingestUserEmail },
                            }
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Stores a pruned OTLP traces request into the raw store.
    /// </summary>
    /// <returns>The count of accepted spans, or 0 if none match.</returns>
    public async Task<int> StoreTracesAsync(
        byte[] payload,
        string ingestUserId,
        string ingestOrganizationId,
        CancellationToken cancellationToken
    )
    {
        var request = ExportTraceServiceRequest.Parser.ParseFrom(payload);
        var pruned = spanFilter.PruneAcceptedSpansInPlace(request);

        if (pruned == 0)
        {
            return 0;
        }

        var metadata = metadataExtractor.ExtractTracesMetadata(request, ingestUserId, pruned);
        // NOTE: Keep this bounded receive span on the ingest path for rollout diagnostics.
        // Central OpenTelemetry sampling controls its cost; tags exclude payload and identity values.
        using var activity = ZeeqTelemetry.Tracer.StartActivity("telemetry.ingest.traces");
        activity?.SetTag("telemetry.signal_type", "traces");
        activity?.SetTag("telemetry.record_count", pruned);
        activity?.SetTag("telemetry.harness", metadata.Harness);

        await rawStore.StoreTracesAsync(
            request.ToByteArray(),
            TelemetrySignalType.Traces,
            metadata,
            ingestUserId,
            ingestOrganizationId,
            cancellationToken
        );

        log.LogDebug(
            "Stored {SpanCount} trace spans from {Harness} via {IngestUserId}",
            pruned,
            metadata.Harness,
            ingestUserId
        );

        return pruned;
    }
}
