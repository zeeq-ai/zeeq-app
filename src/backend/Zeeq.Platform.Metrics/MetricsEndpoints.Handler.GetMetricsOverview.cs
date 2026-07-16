using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves the overview stat-card numbers for the current window, cached for 30s.</summary>
public sealed class GetMetricsOverviewHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Validates the window, then returns the cached overview numbers.</summary>
    public async Task<Results<Ok<MetricsOverview>, BadRequest<MetricsEndpointError>>> HandleAsync(
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

        var key = MetricsEndpointCache.Key(organizationId, "overview", parsedWindow.ToString());

        var result = await cache.GetOrCreateAsync(
            key,
            async token => await store.GetOverviewAsync(organizationId, parsedWindow, token),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
