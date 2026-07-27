using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles listing PR targets for reviewer-agent test runs.
/// </summary>
public sealed class ListCodeReviewAgentTestTargetsHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    IPullRequestRecordStore pullRequests
) : IEndpointHandler
{
    /// <summary>
    /// Lists recent repository PRs in any provider state for back-testing draft agents.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewAgentTestTargetListResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
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

        var access = await authorization.ResolveAsync(organizationId, user, cancellationToken);
        if (access is null)
        {
            return TypedResults.NotFound();
        }

        if (!access.CanManage)
        {
            return TypedResults.Forbid();
        }

        var repository = await repositories.FindActiveForOrganizationAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );
        if (repository is null)
        {
            return TypedResults.NotFound();
        }

        if (
            repository.OrganizationId != organizationId
            || !string.Equals(repository.Id, repositoryId, StringComparison.Ordinal)
        )
        {
            return TypedResults.NotFound();
        }

        var page = await pullRequests.ListRecentAsync(
            new PullRequestStreamQuery(
                OrganizationId: organizationId,
                TeamId: repository.TeamId,
                RepositoryId: repository.Id,
                ClaimStatus: null,
                SubjectUserId: null,
                Cursor: CodeReviewEndpointMapping.ToStreamCursor(cursorCreatedAtUtc, cursorId),
                PageSize: pageSize ?? 25
            ),
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewAgentTestTargetListResponse(
                page.Items.Select(CodeReviewEndpointMapping.ToDto).ToArray(),
                CodeReviewEndpointMapping.ToDto(page.NextCursor),
                CodeReviewEndpointMapping.ToDto(page.NewestCursor)
            )
        );
    }
}
