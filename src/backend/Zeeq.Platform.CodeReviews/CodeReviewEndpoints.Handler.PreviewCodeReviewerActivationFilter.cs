namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles previewing draft reviewer-agent activation filters against user supplied paths.
/// </summary>
public sealed class PreviewCodeReviewerActivationFilterHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories
) : IEndpointHandler
{
    private const int MaxFilePaths = 25;
    private const int MaxFilePathLength = 1024;

    /// <summary>
    /// Applies saved repository filters first, then draft agent activation filters.
    /// </summary>
    /// <remarks>
    /// The management UI previews unsaved agent activation rules, but repository
    /// filters are global review scope and must come from the saved server-side
    /// configuration. This mirrors real review execution: repository file scope
    /// is computed before reviewer agents are resolved.
    /// </remarks>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<PreviewCodeReviewFileFilterResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        PreviewCodeReviewerActivationFilterRequest request,
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

        var candidatePaths = NormalizePaths(request.FilePaths);
        var validationError = Validate(request, candidatePaths);
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

        var files = candidatePaths
            .Select(path => new CodeReviewFileSnapshot(
                path,
                PreviousPath: null,
                CodeReviewFileMutationState.Modified,
                Patch: string.Empty
            ))
            .ToArray();
        var repositoryScope = CodeReviewFileFilterEvaluator.Apply(
            files,
            repository.ReviewConfiguration.FileFilter
        );
        var activationConfiguration = CodeReviewEndpointMapping.ToModel(
            request.ActivationConfiguration
        );
        var activatedFiles = repositoryScope
            .InScopeFiles.Where(file =>
                CodeReviewerAgentResolver.IsFileIncluded(file, activationConfiguration)
            )
            .ToArray();
        var activatedPathSet = activatedFiles
            .Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedFiles = files.Where(file => !activatedPathSet.Contains(file.Path)).ToArray();

        return TypedResults.Ok(
            new PreviewCodeReviewFileFilterResponse(
                [.. activatedFiles.Select(file => file.Path)],
                [.. excludedFiles.Select(file => file.Path)]
            )
        );
    }

    private static CodeReviewEndpointError? Validate(
        PreviewCodeReviewerActivationFilterRequest request,
        IReadOnlyList<string> candidatePaths
    )
    {
        if (request.ActivationConfiguration is null)
        {
            return new CodeReviewEndpointError(
                "invalid_activation_configuration",
                "Activation configuration is required."
            );
        }

        var activationError = CodeReviewerAgentEndpointValidation.ValidateActivationConfiguration(
            request.ActivationConfiguration
        );
        if (activationError is not null)
        {
            return activationError;
        }

        if (request.FilePaths is null || request.FilePaths.Count > MaxFilePaths)
        {
            return new CodeReviewEndpointError(
                "invalid_file_paths",
                $"Provide between 0 and {MaxFilePaths} file paths."
            );
        }

        if (request.FilePaths.Any(path => path is null))
        {
            return new CodeReviewEndpointError("invalid_file_paths", "File paths cannot be null.");
        }

        if (candidatePaths.Any(path => path.Length > MaxFilePathLength))
        {
            return new CodeReviewEndpointError(
                "invalid_file_paths",
                $"File paths cannot exceed {MaxFilePathLength} characters."
            );
        }

        return null;
    }

    // NOTE: Preview paths intentionally follow the existing repository-filter preview behavior:
    // normalize and dedupe freeform user input before evaluation so UI and API previews stay in parity.
    private static string[] NormalizePaths(IReadOnlyList<string>? paths) =>
        paths
            ?.Where(path => path is not null)
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
        ?? [];

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');
}
