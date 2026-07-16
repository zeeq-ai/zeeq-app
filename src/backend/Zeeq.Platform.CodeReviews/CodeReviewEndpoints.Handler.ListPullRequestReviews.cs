namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles review-history loading for one selected pull request.
/// </summary>
public sealed class ListPullRequestCodeReviewsHandler(
    CodeReviewAuthorization authorization,
    IPullRequestRecordStore pullRequests,
    ICodeReviewRecordStore reviews
) : IEndpointHandler
{
    /// <summary>
    /// Lists review rows for one pull request with a partition-aware cursor.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewPullRequestReviewListResponse>
        >
    > HandleAsync(
        string organizationId,
        string pullRequestRecordId,
        DateTimeOffset? createdAtUtc,
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

        if (createdAtUtc is null)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "missing_created_at",
                    "createdAtUtc is required for partition-aware review history lookup."
                )
            );
        }

        if (await authorization.ResolveAsync(organizationId, user, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var pullRequest = await pullRequests.FindAsync(
            pullRequestRecordId,
            createdAtUtc.Value,
            cancellationToken
        );
        if (pullRequest is null || pullRequest.OrganizationId != organizationId)
        {
            return TypedResults.NotFound();
        }

        var page = await reviews.ListForPullRequestAsync(
            new PullRequestReviewStreamQuery(
                OrganizationId: organizationId,
                PullRequestRecordId: pullRequestRecordId,
                PullRequestCreatedAtUtc: createdAtUtc.Value,
                Cursor: CodeReviewEndpointMapping.ToStreamCursor(cursorCreatedAtUtc, cursorId),
                PageSize: pageSize ?? 50
            ),
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewPullRequestReviewListResponse(
                page.Items.Select(CodeReviewEndpointMapping.ToDto).ToArray(),
                CodeReviewEndpointMapping.ToDto(page.NextCursor),
                CodeReviewEndpointMapping.ToDto(page.NewestCursor)
            )
        );
    }
}
