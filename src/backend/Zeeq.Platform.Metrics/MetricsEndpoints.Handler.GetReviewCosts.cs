using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves per-review and author cost series from partitioned metric events.</summary>
public sealed class GetReviewCostsHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Validates the selected window and returns cached review cost series.</summary>
    public async Task<Results<Ok<ReviewCostMetrics>, BadRequest<MetricsEndpointError>>> HandleAsync(
        string organizationId,
        string? window,
        CancellationToken cancellationToken
    )
    {
        if (!MetricWindowQuery.TryParse(window, out var parsedWindow))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError("invalid_window", $"Unknown window '{window}'.")
            );
        }

        var result = await cache.GetOrCreateAsync(
            MetricsEndpointCache.Key(organizationId, "reviews.costs", parsedWindow.ToString()),
            async token =>
                await store.GetReviewCostMetricsAsync(organizationId, parsedWindow, token),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
