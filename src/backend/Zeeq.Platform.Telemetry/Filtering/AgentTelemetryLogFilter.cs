using Zeeq.Platform.Telemetry.Adapters;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpLog = OpenTelemetry.Proto.Logs.V1;

namespace Zeeq.Platform.Telemetry.Filtering;

/// <summary>
/// Defensive log filter — a coordinator that delegates harness-specific keep/drop
/// decisions to registered <see cref="ITelemetryLogFilter"/> implementations.
/// </summary>
/// <remarks>
/// In-place protobuf mutation: prunes unwanted log records from ResourceLogs,
/// returning the count of kept records. The collector's OTTL filter is the first
/// layer; this is the server-side defensive backstop so a collector misconfig
/// cannot leak noise into raw storage.
///
/// Harness-specific logic lives in the adapter chain (Claude, Codex, etc.)
/// implementing <see cref="ITelemetryLogFilter"/>. This coordinator handles only
/// the structural pruning — the compact-toward-front protobuf mutation — while
/// the decision of whether to keep a record is delegated to the registered
/// filters.
/// </remarks>
/// <remarks>
/// Creates the filter with the registered chain of harness-specific filters.
/// </remarks>
public sealed class AgentTelemetryLogFilter(IEnumerable<ITelemetryLogFilter> filters)
{
    private readonly ITelemetryLogFilter[] _filters = [.. filters];

    /// <summary>
    /// Prunes non-session log records in-place and returns the count of accepted records.
    /// </summary>
    public int PruneAcceptedLogsInPlace(ExportLogsServiceRequest request)
    {
        var keptCount = 0;

        foreach (var resourceLog in request.ResourceLogs)
        {
            var serviceName =
                resourceLog
                    .Resource?.Attributes.FirstOrDefault(a => a.Key == "service.name")
                    ?.Value?.StringValue
                ?? "";

            foreach (var scopeLog in resourceLog.ScopeLogs)
            {
                var writeIdx = 0;
                for (var i = 0; i < scopeLog.LogRecords.Count; i++)
                {
                    var record = scopeLog.LogRecords[i];
                    if (ShouldKeepLogRecord(record, serviceName))
                    {
                        scopeLog.LogRecords[writeIdx++] = record;
                        keptCount++;
                    }
                }

                for (var j = scopeLog.LogRecords.Count - 1; j >= writeIdx; j--)
                {
                    scopeLog.LogRecords.RemoveAt(j);
                }
            }
        }

        return keptCount;
    }

    /// <summary>
    /// Delegates the keep/drop decision to the filter that owns the service.
    /// Unknown harnesses are kept for passthrough (future Cursor, OpenCode, etc.);
    /// recognized harnesses are retained only when their filter accepts the record.
    /// </summary>
    private bool ShouldKeepLogRecord(OtlpLog.LogRecord record, string serviceName)
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            return false;
        }

        var hasOwningFilter = false;

        foreach (var filter in _filters)
        {
            if (!filter.HandlesService(serviceName))
            {
                continue;
            }

            hasOwningFilter = true;
            return filter.ShouldKeepLogRecord(record, serviceName);
        }

        // Unknown harness — keep for passthrough.
        return !hasOwningFilter;
    }
}
