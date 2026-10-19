using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves the overview stat-card numbers for the current window, cached for 30s.</summary>
public sealed class GetMetricsOverviewHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Validates the window, then returns the cached overview numbers.</summary>
    public async Task<Results<Ok<MetricsOverview>, BadRequest<MetricsEndpointError>>> HandleAsync(
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

        var key = MetricsEndpointCache.Key(organizationId, "overview", rangeIdentity);

        var result = await cache.GetOrCreateAsync(
            key,
            async token => await store.GetOverviewAsync(organizationId, range, token),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
