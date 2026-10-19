using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>Serves bucketed review volume from code_review_records (UI-4/UI-5), cached for 30s.</summary>
public sealed class GetReviewVolumeHandler(IMetricsQueryStore store, HybridCache cache)
    : IEndpointHandler
{
    /// <summary>Validates the window, then returns the cached review-volume series.</summary>
    public async Task<
        Results<Ok<ReviewVolumePoint[]>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string? window,
        string? startDate,
        string? endDate,
        string[]? repositoryIds,
        string[]? authorLogins,
        CodeReviewRequestOrigin? origin,
        ReviewVolumeGroup groupBy,
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
            "reviews.volume",
            rangeIdentity,
            groupBy.ToString(),
            origin?.ToString(),
            MetricsEndpointCache.Join(repositoryIds),
            MetricsEndpointCache.Join(authorLogins)
        );

        var result = await cache.GetOrCreateAsync(
            key,
            async token =>
                (
                    await store.GetReviewVolumeSeriesAsync(
                        organizationId,
                        range,
                        repositoryIds,
                        authorLogins,
                        origin,
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
