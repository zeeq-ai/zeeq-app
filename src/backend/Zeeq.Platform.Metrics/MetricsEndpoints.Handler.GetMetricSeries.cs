using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves a bucketed metric series (UI-1/UI-2/UI-6), cached per org+query for 30s.</summary>
public sealed class GetMetricSeriesHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Validates the window and metric type, then returns the cached bucketed series.</summary>
    public async Task<
        Results<Ok<MetricSeriesPoint[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string metricType,
        string? window,
        string? startDate,
        string? endDate,
        MetricSeriesGroup groupBy,
        string[]? users,
        string[]? tools,
        string[]? libraries,
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

        if (!MetricTaxonomy.SeriesTypes.Contains(metricType))
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
            "series",
            metricType,
            rangeIdentity,
            groupBy.ToString(),
            MetricsEndpointCache.Join(users),
            MetricsEndpointCache.Join(tools),
            MetricsEndpointCache.Join(libraries)
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetSeriesAsync(
                        organizationId,
                        metricType,
                        range,
                        groupBy,
                        new MetricSeriesFilters(users, tools, libraries),
                        token
                    )
                ).ToArray(),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
