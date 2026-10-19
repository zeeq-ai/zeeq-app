using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

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
        string? startDate,
        string? endDate,
        string[]? repositoryIds,
        string[]? authorLogins,
        ReviewFindingsGroup groupBy,
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

        var key = MetricsEndpointCache.Key(
            organizationId,
            "reviews.findings",
            rangeIdentity,
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
                        range,
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
