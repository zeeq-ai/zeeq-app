namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles saving repository-level code-review configuration.
/// </summary>
public sealed class SaveRepositoryReviewConfigurationHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories
) : IEndpointHandler
{
    /// <summary>
    /// Replaces the typed review configuration for one configured repository.
    /// </summary>
    /// <remarks>
    /// Repository configuration is stored as a typed JSONB document on
    /// <see cref="Zeeq.Core.Models.CodeRepository"/>. The handler preserves
    /// repository identity, display state, and enabled/disabled state, and only
    /// replaces the review configuration document.
    /// </remarks>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewRepositoryConfigurationResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        SaveCodeReviewRepositoryConfigurationRequest request,
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

        var validationError = Validate(request.Configuration);
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

        repository.ReviewConfiguration = CodeReviewEndpointMapping.ToModel(request.Configuration);
        repository.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var saved = await repositories.UpsertAsync(repository, cancellationToken);

        return TypedResults.Ok(
            new CodeReviewRepositoryConfigurationResponse(
                saved.Id,
                CodeReviewEndpointMapping.ToDto(saved.ReviewConfiguration)
            )
        );
    }

    /// <summary>
    /// Maximum length of the repository-level shared prompt fragment. Mirrors
    /// the cap on <see cref="CreateCodeReviewerAgentRequest.Prompt"/> since both
    /// feed into the same reviewer prompt.
    /// </summary>
    private const int MaxSharedPromptFragmentLength = 20_000;

    private static CodeReviewEndpointError? Validate(
        CodeReviewRepositoryConfigurationDto configuration
    )
    {
        if (configuration.FileFilter is null)
        {
            return new CodeReviewEndpointError(
                "invalid_file_filter",
                "File filter configuration is required."
            );
        }

        var includedFiles = configuration.FileFilter.IncludedFiles ?? [];
        var excludedFiles = configuration.FileFilter.ExcludedFiles ?? [];
        var invalidRule = includedFiles
            .Concat(excludedFiles)
            .FirstOrDefault(rule =>
                string.IsNullOrWhiteSpace(rule.Pattern) || !Enum.IsDefined(rule.MatchType)
            );

        if (invalidRule is not null)
        {
            return new CodeReviewEndpointError(
                "invalid_file_filter",
                "File filter patterns cannot be empty."
            );
        }

        if ((configuration.SharedPromptFragment?.Length ?? 0) > MaxSharedPromptFragmentLength)
        {
            return new CodeReviewEndpointError(
                "invalid_shared_prompt_fragment",
                $"Shared prompt fragment cannot exceed {MaxSharedPromptFragmentLength} characters."
            );
        }

        return null;
    }
}
