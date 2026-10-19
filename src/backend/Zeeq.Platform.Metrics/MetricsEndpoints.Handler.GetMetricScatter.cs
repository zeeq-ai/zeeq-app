using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

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
        string? startDate,
        string? endDate,
        string? repositoryId,
        string? facet,
        int? limit,
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

        var boundedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var key = MetricsEndpointCache.Key(
            organizationId,
            "scatter",
            metricType,
            rangeIdentity,
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
                        range,
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
