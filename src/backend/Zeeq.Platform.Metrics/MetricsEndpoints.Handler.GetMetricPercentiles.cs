using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves per-bucket p50/p95/p99 for a histogram metric (UI-8/UI-9), cached for 30s.</summary>
public sealed class GetMetricPercentilesHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Validates the window and histogram type, then returns the cached percentile series.</summary>
    public async Task<
        Results<Ok<MetricPercentilePoint[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string metricType,
        string? window,
        string? repositoryId,
        string? facet,
        CancellationToken cancellationToken
    )
    {
        if (!MetricWindowQuery.TryParse(window, out var parsedWindow))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError("invalid_window", $"Unknown window '{window}'.")
            );
        }

        if (!MetricTaxonomy.HistogramTypes.Contains(metricType))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError(
                    "unknown_metric_type",
                    $"Unknown metric type '{metricType}'."
                )
            );
        }

        var key = MetricsEndpointCache.Key(
            organizationId,
            "percentiles",
            metricType,
            parsedWindow.ToString(),
            repositoryId,
            facet
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetPercentileSeriesAsync(
                        organizationId,
                        metricType,
                        parsedWindow,
                        repositoryId,
                        facet,
                        token
                    )
                ).ToArray(),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
