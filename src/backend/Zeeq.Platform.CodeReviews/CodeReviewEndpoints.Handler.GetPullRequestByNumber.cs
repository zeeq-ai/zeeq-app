namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Resolves a single pull request by its repo-scoped provider number.
/// </summary>
/// <remarks>
/// Used by Mode 2 deep-link (inbox number lookup) and the
/// <c>GET /pull-requests/by-number</c> endpoint. Returns the resolved PR
/// using the existing <see cref="CodeReviewPullRequestDetailResponse"/> so
/// the frontend can inject the row into the inbox and call the existing
/// <c>selectPullRequest</c> path for review loading — no new response type.
///
/// PR numbers are only unique within a repository; <c>repositoryId</c>
/// is required and the lookup is org-scoped, so a wrong org or wrong repo
/// returns 404 without leaking cross-org data.
/// </remarks>
public sealed class GetPullRequestByNumberHandler(
    CodeReviewAuthorization authorization,
    IPullRequestLookupStore lookups,
    IPullRequestRecordStore pullRequests
) : IEndpointHandler
{
    /// <summary>
    /// Resolves a PR by repo-scoped provider number and returns its detail row.
    /// </summary>
    /// <param name="organizationId">Org that owns the pull request.</param>
    /// <param name="repositoryId">Repository scope; required because PR numbers are not globally unique.</param>
    /// <param name="pullRequestNumber">Provider pull request number (GitHub PR #).</param>
    /// <param name="user">Authenticated caller used for org-membership authorization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>200 Ok</c> with the resolved <see cref="CodeReviewPullRequestDetailResponse"/>,
    /// <c>400 Bad Request</c> on missing required parameters, or <c>404 Not Found</c>
    /// when authorization fails or the lookup/record row does not exist.
    /// </returns>
    public async Task<
        Results<
            NotFound,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewPullRequestDetailResponse>
        >
    > HandleAsync(
        string organizationId,
        string? repositoryId,
        int? pullRequestNumber,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("missing_organization", "Organization id is required.")
            );
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "missing_repository",
                    "repositoryId is required (PR numbers are repo-scoped)."
                )
            );
        }

        if (pullRequestNumber is not { } number || number <= 0)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "missing_pull_request_number",
                    "pullRequestNumber is required."
                )
            );
        }

        if (number > 999_999)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_pull_request_number",
                    "pullRequestNumber must be between 1 and 999999."
                )
            );
        }

        if (await authorization.ResolveAsync(organizationId, user, ct) is null)
        {
            return TypedResults.NotFound();
        }

        // O(1) lookup via the non-partitioned PK (org, repo, number).
        // FindAsync is org-scoped; a wrong org or wrong repo returns null (no cross-org leak).
        var lookup = await lookups.FindAsync(organizationId, repositoryId, number, ct);

        if (lookup is null)
        {
            return TypedResults.NotFound();
        }

        var pr = await pullRequests.FindAsync(
            lookup.PullRequestRecordId,
            lookup.PullRequestCreatedAtUtc,
            ct
        );

        if (pr is null || pr.OrganizationId != organizationId)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(
            new CodeReviewPullRequestDetailResponse(CodeReviewEndpointMapping.ToDto(pr))
        );
    }
}
