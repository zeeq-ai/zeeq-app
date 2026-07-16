namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles the UI bypass-check endpoint.
/// Any authenticated org member may bypass the check.
/// </summary>
public sealed class BypassCheckRunHandler(
    CodeReviewAuthorization authorization,
    ICheckRunService checkRunService
) : IEndpointHandler
{
    /// <summary>
    /// Clears a blocking check run for the specified pull request.
    /// </summary>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<BypassCheckRunResponse>, StatusCodeHttpResult>
    > HandleAsync(
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
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

        var access = await authorization.ResolveAsync(
            organizationId,
            user,
            cancellationToken
        );
        if (access is null)
        {
            return TypedResults.NotFound();
        }

        var userId = access.UserId ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var removedBy = $"zeeq-user:{userId}";

        var outcome = await checkRunService.BypassAsync(
            organizationId,
            repositoryId,
            pullRequestNumber,
            removedBy,
            cancellationToken
        );

        return outcome switch
        {
            CheckRunBypassOutcome.Cleared => TypedResults.Ok(
                new BypassCheckRunResponse(PullRequestNumber: pullRequestNumber, Cleared: true)
            ),
            CheckRunBypassOutcome.PrNotFound => TypedResults.NotFound(),
            CheckRunBypassOutcome.Failed => TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => TypedResults.Ok(
                new BypassCheckRunResponse(PullRequestNumber: pullRequestNumber, Cleared: false)
            ),
        };
    }
}
