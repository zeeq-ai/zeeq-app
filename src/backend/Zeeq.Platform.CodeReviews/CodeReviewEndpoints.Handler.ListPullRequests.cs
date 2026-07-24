using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles the PR inbox list endpoint.
/// </summary>
public sealed class ListCodeReviewPullRequestsHandler(
    CodeReviewAuthorization authorization,
    IPullRequestRecordStore pullRequests
) : IEndpointHandler
{
    /// <summary>
    /// Lists recent pull request rows using a partition-aware seek cursor.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewPullRequestListResponse>
        >
    > HandleAsync(
        string organizationId,
        string? teamId,
        string? repositoryId,
        PullRequestClaimStatus? claimStatus,
        CodeReviewInboxScope? scope,
        DateTimeOffset? cursorCreatedAtUtc,
        string? cursorId,
        int? pageSize,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("missing_organization", "Organization id is required.")
            );
        }

        var normalizedTeamId = CodeReviewEndpointMapping.NormalizeOptionalFilter(teamId);
        var normalizedRepositoryId = CodeReviewEndpointMapping.NormalizeOptionalFilter(
            repositoryId
        );
        var effectiveScope = scope ?? CodeReviewInboxScope.All;
        var effectiveSubjectUserId =
            effectiveScope == CodeReviewInboxScope.Mine
                ? user.FindFirstValue(OpenIddictConstants.Claims.Subject)
                : null;
        if (
            effectiveScope == CodeReviewInboxScope.Mine
            && string.IsNullOrWhiteSpace(effectiveSubjectUserId)
        )
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "missing_subject",
                    "Mine scope requires an authenticated user subject."
                )
            );
        }

        if (await authorization.ResolveAsync(organizationId, user, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var page = await pullRequests.ListRecentAsync(
            new PullRequestStreamQuery(
                OrganizationId: organizationId,
                TeamId: normalizedTeamId,
                RepositoryId: normalizedRepositoryId,
                ClaimStatus: claimStatus,
                SubjectUserId: effectiveSubjectUserId,
                Cursor: CodeReviewEndpointMapping.ToStreamCursor(cursorCreatedAtUtc, cursorId),
                PageSize: pageSize ?? 50
            ),
            cancellationToken
        );
        var reviewUpdatesCursor = BuildInitialReviewUpdatesCursor(
            page,
            normalizedTeamId,
            normalizedRepositoryId,
            effectiveScope,
            effectiveSubjectUserId
        );

        return TypedResults.Ok(
            new CodeReviewPullRequestListResponse(
                page.Items.Select(CodeReviewEndpointMapping.ToDto).ToArray(),
                CodeReviewEndpointMapping.ToDto(page.NextCursor),
                CodeReviewEndpointMapping.ToDto(page.NewestCursor),
                CodeReviewEndpointMapping.ToDto(reviewUpdatesCursor)
            )
        );
    }

    private static CodeReviewUpdateCursor? BuildInitialReviewUpdatesCursor(
        CodeReviewStreamPage<PullRequestRecord> page,
        string? teamId,
        string? repositoryId,
        CodeReviewInboxScope scope,
        string? subjectUserId
    )
    {
        if (page.Items.Count == 0)
        {
            return null;
        }

        var lowerBound = page.Items.Min(item => item.CreatedAtUtc);

        // The PR list only establishes the inbox window. Existing review state is
        // loaded through review history or an initial update-feed call; polling
        // from this high-water cursor catches review changes after the list loads.
        return new(
            lowerBound,
            DateTimeOffset.UtcNow,
            DateTimeOffset.MinValue,
            CodeReviewUpdateCursor.SyntheticHighWaterId,
            teamId,
            repositoryId,
            scope,
            subjectUserId
        );
    }
}
