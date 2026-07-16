using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

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
        MetricSeriesGroup groupBy,
        string[]? users,
        string[]? tools,
        string[]? libraries,
        CancellationToken cancellationToken
    )
    {
        if (!MetricWindowQuery.TryParse(window, out var parsedWindow))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError("invalid_window", $"Unknown window '{window}'.")
            );
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
            parsedWindow.ToString(),
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
                        parsedWindow,
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
