namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles previewing draft repository-level file filters against user supplied paths.
/// </summary>
public sealed class PreviewCodeReviewFileFilterHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories
) : IEndpointHandler
{
    private const int MaxFilePaths = 25;
    private const int MaxFilePathLength = 1024;

    /// <summary>
    /// Applies the production repository file-scope evaluator to draft rules and sample paths.
    /// </summary>
    /// <remarks>
    /// The preview intentionally calls <see cref="CodeReviewFileFilterEvaluator"/> instead of
    /// duplicating matcher logic in the UI or API handler. This keeps the management screen aligned
    /// with real review execution, including built-in default exclusions for lockfiles, generated
    /// output, vendored dependencies, editor files, and other common noise.
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
        PreviewCodeReviewFileFilterRequest request,
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
        var scope = CodeReviewFileFilterEvaluator.Apply(
            files,
            CodeReviewEndpointMapping.ToModel(request.FileFilter)
        );

        return TypedResults.Ok(
            new PreviewCodeReviewFileFilterResponse(
                [.. scope.InScopeFiles.Select(file => file.Path)],
                [.. scope.OutOfScopeFiles.Select(file => file.Path)]
            )
        );
    }

    private static CodeReviewEndpointError? Validate(
        PreviewCodeReviewFileFilterRequest request,
        IReadOnlyList<string> candidatePaths
    )
    {
        if (request.FileFilter is null)
        {
            return new CodeReviewEndpointError(
                "invalid_file_filter",
                "File filter configuration is required."
            );
        }

        var includedFiles = request.FileFilter.IncludedFiles ?? [];
        var excludedFiles = request.FileFilter.ExcludedFiles ?? [];
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
