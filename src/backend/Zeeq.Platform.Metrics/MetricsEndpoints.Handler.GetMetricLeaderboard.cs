using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

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
        string? startDate,
        string? endDate,
        string? library,
        int? top,
        string[]? users,
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

        var boundedTop = Math.Clamp(top ?? DefaultTop, 1, MaxTop);
        var key = MetricsEndpointCache.Key(
            organizationId,
            "leaderboard",
            rangeIdentity,
            library,
            boundedTop.ToString(),
            MetricsEndpointCache.Join(users)
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetLeaderboardAsync(
                        organizationId,
                        MetricTaxonomy.LeaderboardTypes,
                        range,
                        library,
                        boundedTop,
                        token,
                        users
                    )
                ).ToArray(),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
