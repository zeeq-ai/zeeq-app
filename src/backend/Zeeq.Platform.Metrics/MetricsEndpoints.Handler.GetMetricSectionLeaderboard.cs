using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Serves the top-N read-section leaderboard (path + heading), cached 30s. Scoped to exactly one
/// kind (section prose or code snippet) per call — see <see cref="MetricTaxonomy.SectionLeaderboardTypes" />
/// for why the two must never be combined in one query.
/// </summary>
public sealed class GetMetricSectionLeaderboardHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    private const int DefaultTop = 10;
    private const int MaxTop = 100;

    /// <summary>Validates the window and kind, then returns the cached top-N section leaderboard.</summary>
    public async Task<
        Results<Ok<MetricLeaderboardItem[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string? window,
        string? startDate,
        string? endDate,
        string kind,
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

        var metricTypes = kind switch
        {
            "section" => MetricTaxonomy.SectionLeaderboardTypes,
            "code" => MetricTaxonomy.SnippetLeaderboardTypes,
            _ => null,
        };

        if (metricTypes is null)
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError(
                    "invalid_kind",
                    $"kind must be 'section' or 'code'; got '{kind}'."
                )
            );
        }

        var boundedTop = Math.Clamp(top ?? DefaultTop, 1, MaxTop);
        var key = MetricsEndpointCache.Key(
            organizationId,
            "leaderboard.sections",
            kind,
            rangeIdentity,
            library,
            boundedTop.ToString(),
            MetricsEndpointCache.Join(users)
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetSectionLeaderboardAsync(
                        organizationId,
                        metricTypes,
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
