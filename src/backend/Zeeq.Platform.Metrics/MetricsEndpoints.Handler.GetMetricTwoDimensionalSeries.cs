using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves a bucketed metric series grouped by two dimensions, cached per org+query.</summary>
public sealed class GetMetricTwoDimensionalSeriesHandler(
    IMetricsQueryStore store,
    HybridCache cache
) : IEndpointHandler
{
    /// <summary>Validates the window, metric type, and dimensions, then returns the cached series.</summary>
    public async Task<
        Results<Ok<MetricTwoDimensionalSeriesPoint[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string metricType,
        string? window,
        MetricSeriesGroup primaryGroupBy,
        MetricSeriesGroup secondaryGroupBy,
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

        if (!IsSupportedSeriesGroup(primaryGroupBy) || !IsSupportedSeriesGroup(secondaryGroupBy))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError(
                    "invalid_group_by",
                    "Two-dimensional series queries require two non-empty group dimensions."
                )
            );
        }

        if (primaryGroupBy == secondaryGroupBy)
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError(
                    "invalid_group_by",
                    "Two-dimensional series queries require distinct group dimensions."
                )
            );
        }

        var key = MetricsEndpointCache.Key(
            organizationId,
            "series-two-dimensional",
            metricType,
            parsedWindow.ToString(),
            primaryGroupBy.ToString(),
            secondaryGroupBy.ToString(),
            MetricsEndpointCache.Join(users),
            MetricsEndpointCache.Join(tools),
            MetricsEndpointCache.Join(libraries)
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetTwoDimensionalSeriesAsync(
                        organizationId,
                        metricType,
                        parsedWindow,
                        primaryGroupBy,
                        secondaryGroupBy,
                        new MetricSeriesFilters(users, tools, libraries),
                        token
                    )
                ).ToArray(),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }

    private static bool IsSupportedSeriesGroup(MetricSeriesGroup group) =>
        group
            is MetricSeriesGroup.User
                or MetricSeriesGroup.Tool
                or MetricSeriesGroup.Library
                or MetricSeriesGroup.UserAgent
                or MetricSeriesGroup.Model;
}
