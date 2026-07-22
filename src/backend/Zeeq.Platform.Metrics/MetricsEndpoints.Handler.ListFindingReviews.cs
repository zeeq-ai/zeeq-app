using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Serves the findings drill-down list behind the Critical/Major stat cards: newest-first,
/// keyset-paginated review groups with at least one finding of the requested severity.
/// </summary>
/// <remarks>
/// Deliberately not cached like the other metrics endpoints (<see cref="MetricsEndpointCache" />) —
/// caching a cursor-paginated result by <c>(severity, cursor, limit)</c> risks serving a page that no
/// longer lines up with a since-superseded row (a follow-up review landing between two page fetches),
/// and this is a click-to-open list rather than a polled dashboard tile, so the staleness tradeoff
/// that makes the 30s cache worthwhile elsewhere doesn't apply here.
/// </remarks>
public sealed class ListFindingReviewsHandler(
    IMetricsQueryStore store,
    CodeReviewRequestLinkFactory linkFactory
) : IEndpointHandler
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    /// <summary>Validates the window/cursor, then returns one page of review groups.</summary>
    public async Task<
        Results<Ok<FindingReviewListResponse>, BadRequest<MetricsEndpointError>>
    > HandleAsync(
        string organizationId,
        string? window,
        FindingSeverity severity,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken
    )
    {
        if (!MetricWindowQuery.TryParse(window, out var parsedWindow))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError("invalid_window", $"Unknown window '{window}'.")
            );
        }

        // Enum query binding accepts any integer-parseable value, not just the two documented
        // members — reject anything outside the closed Critical|Major contract before it reaches
        // the store, rather than letting the store's severity-column switch silently pick one.
        if (!Enum.IsDefined(severity))
        {
            return TypedResults.BadRequest(
                new MetricsEndpointError(
                    "invalid_severity",
                    $"Unknown severity '{severity}'. Must be Critical or Major."
                )
            );
        }

        DateTimeOffset? cursorCreatedAtUtc = null;
        string? cursorId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (
                !ReviewFindingsListCursor.TryDecode(cursor, out var decodedCreatedAtUtc, out var decodedId)
            )
            {
                return TypedResults.BadRequest(
                    new MetricsEndpointError("invalid_cursor", "Malformed or expired cursor.")
                );
            }

            cursorCreatedAtUtc = decodedCreatedAtUtc;
            cursorId = decodedId;
        }

        var pageSize = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // Fetch one extra row so we can tell "exactly pageSize rows, no more" apart from "exactly
        // pageSize rows, and there's at least one more" — same idiom as the ingest run list.
        var groups = await store.ListFindingReviewGroupsAsync(
            organizationId,
            parsedWindow,
            severity,
            cursorCreatedAtUtc,
            cursorId,
            pageSize + 1,
            cancellationToken
        );

        var hasMore = groups.Count > pageSize;
        var page = hasMore ? groups.Take(pageSize).ToArray() : groups;
        var nextCursor = hasMore
            ? ReviewFindingsListCursor.Encode(page[^1].ReviewCreatedAtUtc, page[^1].ReviewId)
            : null;

        return TypedResults.Ok(
            new FindingReviewListResponse([.. page.Select(ToListItem)], nextCursor)
        );
    }

    /// <summary>
    /// Maps one store row to the response DTO, resolving the deep link: the PR history view when
    /// the group is PR-backed, otherwise the single latest-review view for agent (non-PR) reviews.
    /// </summary>
    private FindingReviewListItemResponse ToListItem(FindingReviewGroup group)
    {
        var url =
            group.PullRequestRecordId is { } pullRequestRecordId
            && group.PullRequestRecordCreatedAtUtc is { } pullRequestRecordCreatedAtUtc
                ? linkFactory.BuildSinglePullRequestLink(
                    pullRequestRecordId,
                    pullRequestRecordCreatedAtUtc
                )
                : linkFactory.BuildSingleReviewLink(
                    group.ReviewId,
                    group.ReviewCreatedAtUtc,
                    CodeReviewSingleViewMode.Agent
                );

        return new FindingReviewListItemResponse(
            group.ReviewId,
            group.Title,
            group.OwnerQualifiedRepoName,
            group.PullRequestNumber,
            group.AuthorLogin,
            group.RequestOrigin,
            group.ReviewCreatedAtUtc,
            group.GroupCriticalFindings,
            group.GroupMajorFindings,
            url
        );
    }
}
