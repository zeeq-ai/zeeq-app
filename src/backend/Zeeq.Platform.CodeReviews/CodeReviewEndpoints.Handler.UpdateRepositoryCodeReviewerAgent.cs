using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles updating persisted reviewer agents for one repository.
/// </summary>
public sealed class UpdateRepositoryCodeReviewerAgentHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewerAgentStore agents
) : IEndpointHandler
{
    /// <summary>
    /// Replaces editable fields on one repository-scoped reviewer agent.
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
        string repositoryId,
        string agentId,
        UpdateCodeReviewerAgentRequest request,
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

        var validationError = CodeReviewerAgentEndpointValidation.Validate(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
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

        existing.TeamId = repository.TeamId;
        existing.DisplayName = request.DisplayName.Trim();
        existing.ReviewFacet = request.ReviewFacet.Trim();
        existing.ModelTier = request.ModelTier;
        existing.Prompt = request.Prompt.Trim();
        existing.Enabled = request.Enabled;
        existing.ActivationConfiguration = CodeReviewEndpointMapping.ToModel(
            request.ActivationConfiguration
        );
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var saved = await agents.UpdateAsync(existing, cancellationToken);

        return TypedResults.Ok(
            new CodeReviewerAgentResponse(CodeReviewEndpointMapping.ToDto(saved))
        );
    }
}
