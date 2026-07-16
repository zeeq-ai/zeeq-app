using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves the top-N read-path leaderboard across section + snippet reads (UI-7), cached 30s.</summary>
public sealed class GetMetricLeaderboardHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    private const int DefaultTop = 10;
    private const int MaxTop = 100;

    /// <summary>Validates the window, then returns the cached top-N path leaderboard.</summary>
    public async Task<
        Results<Ok<MetricLeaderboardItem[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string? window,
        string? library,
        int? top,
        CancellationToken cancellationToken
    )
    {
        if (!MetricWindowQuery.TryParse(window, out var parsedWindow))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError("invalid_window", $"Unknown window '{window}'.")
            );
        }

        var boundedTop = Math.Clamp(top ?? DefaultTop, 1, MaxTop);
        var key = MetricsEndpointCache.Key(
            organizationId,
            "leaderboard",
            parsedWindow.ToString(),
            library,
            boundedTop.ToString()
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetLeaderboardAsync(
                        organizationId,
                        MetricTaxonomy.LeaderboardTypes,
                        parsedWindow,
                        library,
                        boundedTop,
                        token
                    )
                ).ToArray(),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
