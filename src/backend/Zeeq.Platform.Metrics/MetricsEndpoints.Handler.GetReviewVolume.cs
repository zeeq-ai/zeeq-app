using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;

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
        string[]? repositoryIds,
        string[]? authorLogins,
        CodeReviewRequestOrigin? origin,
        ReviewVolumeGroup groupBy,
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
            "reviews.volume",
            parsedWindow.ToString(),
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
                        parsedWindow,
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
