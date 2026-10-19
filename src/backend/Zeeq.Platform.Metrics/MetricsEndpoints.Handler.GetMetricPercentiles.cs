using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

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
        string? startDate,
        string? endDate,
        string? repositoryId,
        string? facet,
        CancellationToken cancellationToken
    )
    {
        if (
            !MetricRangeQuery.TryResolve(
                window,
                startDate,
                endDate,
                DateTimeOffset.UtcNow,
                out var range,
                out var rangeIdentity,
                out var rangeError
            )
        )
        {
            return TypedResults.BadRequest(rangeError);
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
            rangeIdentity,
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
                        range,
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
