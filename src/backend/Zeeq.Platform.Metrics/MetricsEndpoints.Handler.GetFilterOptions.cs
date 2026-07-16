using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Serves the dashboard's distinct filter option lists (users, tools, repositories, authors),
/// cached for 30s. Repository options include soft-deleted rows so historical review data whose
/// mapping was removed stays filterable.
/// </summary>
public sealed class GetMetricsFilterOptionsHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Returns the cached filter options for the organization.</summary>
    public async Task<Ok<MetricsFilterOptions>> HandleAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var key = MetricsEndpointCache.Key(organizationId, "filter-options");

        var result = await cache.GetOrCreateAsync(
            key,
            async token => await store.GetFilterOptionsAsync(organizationId, token),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
