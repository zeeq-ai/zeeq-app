using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves a recent raw-sample scatter for a histogram metric (UI-8/UI-9), cached for 30s.</summary>
public sealed class GetMetricScatterHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 2000;

    /// <summary>Validates the window and histogram type, then returns the cached scatter sample.</summary>
    public async Task<
        Results<Ok<MetricScatterPoint[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string metricType,
        string? window,
        string? repositoryId,
        string? facet,
        int? limit,
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

        var boundedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var key = MetricsEndpointCache.Key(
            organizationId,
            "scatter",
            metricType,
            parsedWindow.ToString(),
            repositoryId,
            facet,
            boundedLimit.ToString()
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetScatterSampleAsync(
                        organizationId,
                        metricType,
                        parsedWindow,
                        repositoryId,
                        facet,
                        boundedLimit,
                        token
                    )
                ).ToArray(),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
