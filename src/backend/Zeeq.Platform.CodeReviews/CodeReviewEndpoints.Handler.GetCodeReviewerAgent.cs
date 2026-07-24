namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles direct lookup of one persisted reviewer agent.
/// </summary>
public sealed class GetCodeReviewerAgentHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewerAgentStore agents
) : IEndpointHandler
{
    /// <summary>
    /// Gets one non-deleted reviewer agent by organization and agent id.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewerAgentResponse>
        >
    > HandleAsync(
        string organizationId,
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

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("missing_agent", "Agent id is required.")
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

        var agent = await agents.FindAsync(organizationId, agentId, cancellationToken);
        if (agent is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new CodeReviewerAgentResponse(CodeReviewEndpointMapping.ToDto(agent)));
    }
}
