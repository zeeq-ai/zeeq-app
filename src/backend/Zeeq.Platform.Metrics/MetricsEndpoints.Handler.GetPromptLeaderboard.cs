using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves the top-N successfully retrieved dynamic prompts, cached 30s.</summary>
public sealed class GetPromptLeaderboardHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    private const int DefaultTop = 10;
    private const int MaxTop = 100;

    /// <summary>Validates the window, then returns the cached top-N prompt leaderboard.</summary>
    public async Task<
        Results<Ok<MetricLeaderboardItem[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string? window,
        string? startDate,
        string? endDate,
        string[]? users,
        string? library,
        int? top,
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
            "leaderboard-prompts",
            rangeIdentity,
            MetricsEndpointCache.Join(users),
            library,
            boundedTop.ToString()
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetPromptLeaderboardAsync(
                        organizationId,
                        range,
                        users,
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
