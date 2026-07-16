namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles listing persisted reviewer agents for one repository.
/// </summary>
public sealed class ListRepositoryCodeReviewerAgentsHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewerAgentStore agents
) : IEndpointHandler
{
    /// <summary>
    /// Lists the repository-scoped persisted reviewer agents.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewerAgentListResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
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

        if (
            await repositories.FindActiveForOrganizationAsync(
                organizationId,
                repositoryId,
                cancellationToken
            )
            is null
        )
        {
            return TypedResults.NotFound();
        }

        var configuredAgents = await agents.ListForRepositoryAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewerAgentListResponse(
                configuredAgents.Select(CodeReviewEndpointMapping.ToDto).ToArray()
            )
        );
    }
}
