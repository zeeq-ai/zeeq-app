using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles creating persisted reviewer agents for one repository.
/// </summary>
public sealed class CreateRepositoryCodeReviewerAgentHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewerAgentStore agents
) : IEndpointHandler
{
    private const int MaxRepositoryAgents = 10;

    /// <summary>
    /// Creates a repository-scoped reviewer agent.
    /// </summary>
    /// <remarks>
    /// Provider names, model names, and credential identifiers are intentionally
    /// absent from this request. Persisted reviewer agents store only the
    /// semantic <see cref="CodeReviewModelTier" />; runtime execution resolves
    /// that tier through organization LLM settings or system defaults.
    /// </remarks>
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
        CreateCodeReviewerAgentRequest request,
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

        var existingAgents = await agents.ListForRepositoryAsync(
            organizationId,
            repository.Id,
            cancellationToken
        );
        if (existingAgents.Count >= MaxRepositoryAgents)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "agent_limit_reached",
                    "A repository can have at most 10 reviewer agents."
                )
            );
        }

        var now = DateTimeOffset.UtcNow;
        var saved = await agents.AddAsync(
            new CodeReviewerAgent
            {
                Id = $"cra_{Guid.CreateVersion7():N}",
                OrganizationId = organizationId,
                TeamId = repository.TeamId,
                RepositoryId = repository.Id,
                DisplayName = request.DisplayName.Trim(),
                ReviewFacet = request.ReviewFacet.Trim(),
                ModelTier = request.ModelTier,
                Prompt = request.Prompt.Trim(),
                Enabled = request.Enabled,
                ActivationConfiguration = CodeReviewEndpointMapping.ToModel(
                    request.ActivationConfiguration
                ),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewerAgentResponse(CodeReviewEndpointMapping.ToDto(saved))
        );
    }
}
