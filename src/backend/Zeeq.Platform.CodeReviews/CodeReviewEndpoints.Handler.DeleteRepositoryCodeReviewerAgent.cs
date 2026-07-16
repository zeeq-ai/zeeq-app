namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles disabling persisted reviewer agents for one repository.
/// </summary>
public sealed class DeleteRepositoryCodeReviewerAgentHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewerAgentStore agents
) : IEndpointHandler
{
    /// <summary>
    /// Soft-disables one repository-scoped reviewer agent.
    /// </summary>
    public async Task<
        Results<NotFound, ForbidHttpResult, BadRequest<CodeReviewEndpointError>, NoContent>
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string agentId,
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

        var existing = await agents.FindAsync(organizationId, agentId, cancellationToken);
        if (existing is null || existing.RepositoryId != repository.Id)
        {
            return TypedResults.NotFound();
        }

        var disabled = await agents.DisableAsync(
            organizationId,
            agentId,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        return disabled ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
