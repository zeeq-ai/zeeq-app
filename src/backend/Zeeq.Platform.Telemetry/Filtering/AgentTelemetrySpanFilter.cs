using Zeeq.Platform.Telemetry.Adapters;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Zeeq.Platform.Telemetry.Filtering;

/// <summary>
/// Defensive span filter — a coordinator that delegates harness-specific keep/drop
/// decisions to registered <see cref="ITelemetrySpanFilter"/> implementations.
/// </summary>
/// <remarks>
/// In-place protobuf mutation: prunes unwanted spans from ResourceSpans,
/// returning the count of kept spans.
///
/// Harness-specific logic lives in the adapter chain (Copilot Chat etc.)
/// implementing <see cref="ITelemetrySpanFilter"/>. This coordinator handles only
/// the structural pruning, delegating keep/drop to the registered filters.
/// Unknown service names are dropped.
/// </remarks>
/// <remarks>
/// Creates the filter with the registered chain of harness-specific filters.
/// </remarks>
public sealed class AgentTelemetrySpanFilter(IEnumerable<ITelemetrySpanFilter> filters)
{
    private readonly ITelemetrySpanFilter[] _filters = [.. filters];

    /// <summary>
    /// Prunes non-session spans in-place and returns the count of accepted spans.
    /// </summary>
    public int PruneAcceptedSpansInPlace(ExportTraceServiceRequest request)
    {
        var keptCount = 0;

        foreach (var resourceSpan in request.ResourceSpans)
        {
            var serviceName =
                resourceSpan
                    .Resource?.Attributes.FirstOrDefault(a => a.Key == "service.name")
                    ?.Value?.StringValue
                ?? "";

            foreach (var scopeSpan in resourceSpan.ScopeSpans)
            {
                var writeIdx = 0;

                for (var i = 0; i < scopeSpan.Spans.Count; i++)
                {
                    if (ShouldKeepSpan(scopeSpan.Spans[i], serviceName))
                    {
                        scopeSpan.Spans[writeIdx++] = scopeSpan.Spans[i];
                        keptCount++;
                    }
                }

                for (var j = scopeSpan.Spans.Count - 1; j >= writeIdx; j--)
                {
                    scopeSpan.Spans.RemoveAt(j);
                }
            }
        }

        return keptCount;
    }

    /// <summary>
    /// Delegates the keep/drop decision to the first matching filter in the chain.
    /// Unknown harnesses are dropped.
    /// </summary>
    private bool ShouldKeepSpan(Span span, string serviceName)
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            return false;
        }

        foreach (var filter in _filters)
        {
            if (filter.ShouldKeepSpan(span, serviceName))
            {
                return true;
            }
        }

        return false;
    }
}
