using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

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
        string kind,
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
            parsedWindow.ToString(),
            library,
            boundedTop.ToString()
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetSectionLeaderboardAsync(
                        organizationId,
                        metricTypes,
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
