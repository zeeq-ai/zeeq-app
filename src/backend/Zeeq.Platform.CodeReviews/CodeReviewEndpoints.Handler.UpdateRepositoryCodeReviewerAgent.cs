using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles updating persisted reviewer agents for one repository.
/// </summary>
/// <remarks>
/// Saving an agent also captures revision history. The pre-change configuration is snapshotted
/// on the first update, and the new configuration is snapshotted atomically with the same
/// update. A status-only or otherwise content-identical save creates no revision noise.
/// </remarks>
public sealed class UpdateRepositoryCodeReviewerAgentHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewerAgentStore agents,
    TimeProvider timeProvider
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

        if (request.Label is { Length: > 200 })
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_label",
                    "A revision label must be at most 200 characters."
                )
            );
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

        var now = timeProvider.GetUtcNow();
        var userId = CodeReviewEvaluationHandlerSupport.UserId(user) ?? string.Empty;

        // Use a detached replacement. The store locks and reloads the persisted original
        // before capturing history, then commits the replacement and revisions together.
        var replacement = new CodeReviewerAgent
        {
            Id = existing.Id,
            OrganizationId = existing.OrganizationId,
            RepositoryId = existing.RepositoryId,
            TeamId = repository.TeamId,
            DisplayName = request.DisplayName.Trim(),
            ReviewFacet = request.ReviewFacet.Trim(),
            ModelTier = request.ModelTier,
            Prompt = request.Prompt.Trim(),
            Enabled = request.Enabled,
            ActivationConfiguration = CodeReviewEndpointMapping.ToModel(
                request.ActivationConfiguration
            ),
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = now,
        };
        var saved = await agents.SaveWithRevisionAsync(
            replacement,
            create: false,
            userId,
            request.Label,
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewerAgentResponse(CodeReviewEndpointMapping.ToDto(saved))
        );
    }
}
