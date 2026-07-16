using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves bucketed finding-severity sums from code_review_records (UI-3), cached for 30s.</summary>
public sealed class GetReviewFindingsHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Validates the window, then returns the cached review-findings series.</summary>
    public async Task<
        Results<Ok<ReviewFindingsPoint[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string? window,
        string[]? repositoryIds,
        string[]? authorLogins,
        ReviewFindingsGroup groupBy,
        CancellationToken cancellationToken
    )
    {
        if (!MetricWindowQuery.TryParse(window, out var parsedWindow))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError("invalid_window", $"Unknown window '{window}'.")
            );
        }

        var key = MetricsEndpointCache.Key(
            organizationId,
            "reviews.findings",
            parsedWindow.ToString(),
            groupBy.ToString(),
            MetricsEndpointCache.Join(repositoryIds),
            MetricsEndpointCache.Join(authorLogins)
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetReviewFindingsSeriesAsync(
                        organizationId,
                        parsedWindow,
                        repositoryIds,
                        authorLogins,
                        groupBy,
                        token
                    )
                ).ToArray(),
            MetricsEndpointCache.Options,
            cancellationToken: cancellationToken
        );

        return TypedResults.Ok(result);
    }
}
