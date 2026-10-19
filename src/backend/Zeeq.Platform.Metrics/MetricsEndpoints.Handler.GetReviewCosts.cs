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
        string? startDate,
        string? endDate,
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

        var result = await cache.GetOrCreateAsync(
            MetricsEndpointCache.Key(organizationId, "reviews.costs", rangeIdentity),
            async token => await store.GetReviewCostMetricsAsync(organizationId, range, token),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
