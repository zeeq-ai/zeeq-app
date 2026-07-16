namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles loading one selected pull request.
/// </summary>
public sealed class GetCodeReviewPullRequestHandler(
    CodeReviewAuthorization authorization,
    IPullRequestRecordStore pullRequests
) : IEndpointHandler
{
    /// <summary>
    /// Gets a pull request by stable ID and partition timestamp (decoded from the single-view token by the endpoint).
    /// </summary>
    public async Task<Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewPullRequestDetailResponse>>> HandleAsync(
        string organizationId,
        string pullRequestRecordId,
        DateTimeOffset createdAtUtc,
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

        if (await authorization.ResolveAsync(organizationId, user, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var pullRequest = await pullRequests.FindAsync(
            pullRequestRecordId,
            createdAtUtc,
            cancellationToken
        );
        if (pullRequest is null || pullRequest.OrganizationId != organizationId)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(
            new CodeReviewPullRequestDetailResponse(CodeReviewEndpointMapping.ToDto(pullRequest))
        );
    }
}
