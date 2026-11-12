using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Shared guards for evaluation endpoints.
/// </summary>
internal static class CodeReviewEvaluationHandlerSupport
{
    /// <summary>Returns the feature-disabled error when evaluations are off.</summary>
    public static CodeReviewEndpointError? FeatureDisabled(CodeReviewSettings settings) =>
        settings.EvaluationsEnabled
            ? null
            : new("feature_disabled", "Reviewer backtesting is not enabled for this deployment.");

    /// <summary>Returns the authenticated subject id.</summary>
    public static string? UserId(ClaimsPrincipal user) =>
        user.FindFirstValue(OpenIddictConstants.Claims.Subject);

    /// <summary>Validates the v1 reference limits before the store is called.</summary>
    public static CodeReviewEndpointError? ValidateReferenceSets(
        IReadOnlyList<CodeReviewEvaluationReferenceSetDto>? referenceSets
    )
    {
        if (referenceSets is null)
        {
            return null;
        }

        foreach (var set in referenceSets)
        {
            if (string.IsNullOrWhiteSpace(set.ReviewerAgentId))
            {
                return new("invalid_request", "Every reference set requires a reviewer agent id.");
            }

            if (!Enum.IsDefined(set.State))
            {
                return new("invalid_request", "The reference state is not supported.");
            }

            if (set.Findings.Count > CodeReviewEvaluationLimits.MaxReferenceFindings)
            {
                return new(
                    "reference_limit_reached",
                    $"A reference set can hold at most {CodeReviewEvaluationLimits.MaxReferenceFindings} findings."
                );
            }

            foreach (var finding in set.Findings)
            {
                if (!Enum.IsDefined(finding.Severity) || !Enum.IsDefined(finding.Origin))
                {
                    return new(
                        "invalid_request",
                        "A reference finding has an unsupported severity or origin."
                    );
                }

                if (string.IsNullOrWhiteSpace(finding.Id))
                {
                    return new("invalid_request", "Every reference finding requires an id.");
                }

                if (
                    string.IsNullOrWhiteSpace(finding.Title)
                    || finding.Title.Length > CodeReviewEvaluationLimits.MaxReferenceTitleLength
                )
                {
                    return new(
                        "invalid_request",
                        $"Reference titles must be between 1 and {CodeReviewEvaluationLimits.MaxReferenceTitleLength} characters."
                    );
                }

                if (
                    string.IsNullOrWhiteSpace(finding.Summary)
                    || finding.Summary.Length > CodeReviewEvaluationLimits.MaxReferenceSummaryLength
                )
                {
                    return new(
                        "invalid_request",
                        $"Reference summaries must be between 1 and {CodeReviewEvaluationLimits.MaxReferenceSummaryLength} characters."
                    );
                }

                if (string.IsNullOrWhiteSpace(finding.File))
                {
                    return new("invalid_request", "Every reference finding requires a file path.");
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Lists evaluation datasets for one repository.
/// </summary>
public sealed class ListCodeReviewEvaluationDatasetsHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets
) : IEndpointHandler
{
    /// <summary>Handles the list request.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewEvaluationDatasetListResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        bool includeArchived,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
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

        var rows = await datasets.ListAsync(
            new(organizationId, repository.Id, includeArchived),
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewEvaluationDatasetListResponse([
                .. rows.Select(CodeReviewEvaluationEndpointMapping.ToSummaryDto),
            ])
        );
    }
}

/// <summary>
/// Creates one persisted evaluation dataset idempotently.
/// </summary>
public sealed class CreateCodeReviewEvaluationDatasetHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    CodeReviewSettings settings,
    TimeProvider timeProvider
) : IEndpointHandler
{
    /// <summary>Handles the create request.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewEvaluationDatasetResponse>,
            Conflict<CodeReviewEndpointError>,
            JsonHttpResult<CodeReviewEndpointError>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        CreateCodeReviewEvaluationDatasetRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (CodeReviewEvaluationHandlerSupport.FeatureDisabled(settings) is { } disabled)
        {
            return TypedResults.Json(disabled, statusCode: StatusCodes.Status503ServiceUnavailable);
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

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > CodeReviewEvaluationLimits.MaxDatasetNameLength)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    $"A dataset name is required and must be at most {CodeReviewEvaluationLimits.MaxDatasetNameLength} characters."
                )
            );
        }

        if (
            request.Description is
            { Length: > CodeReviewEvaluationLimits.MaxDatasetDescriptionLength }
        )
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    $"A dataset description must be at most {CodeReviewEvaluationLimits.MaxDatasetDescriptionLength} characters."
                )
            );
        }

        if (
            string.IsNullOrWhiteSpace(request.CreateRequestKey)
            || request.CreateRequestKey.Length > 128
        )
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "A create request key is required and must be at most 128 characters."
                )
            );
        }

        var now = timeProvider.GetUtcNow();
        CodeReviewEvaluationDataset created;
        try
        {
            created = await datasets.CreateAsync(
                new CodeReviewEvaluationDataset
                {
                    Id = $"cred_{Guid.CreateVersion7():N}",
                    OrganizationId = organizationId,
                    TeamId = repository.TeamId,
                    RepositoryId = repository.Id,
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(request.Description)
                        ? null
                        : request.Description.Trim(),
                    Version = 1,
                    CreateRequestKey = request.CreateRequestKey.Trim(),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                },
                cancellationToken
            );
        }
        catch (CodeReviewEvaluationDatasetRequestKeyConflictException)
        {
            return TypedResults.Conflict(
                new CodeReviewEndpointError(
                    "create_request_key_conflict",
                    "The create request key already belongs to a dataset in another repository."
                )
            );
        }

        var cases = await datasets.ListCasesAsync(
            organizationId,
            created.Id,
            includeRemoved: false,
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewEvaluationDatasetResponse(
                CodeReviewEvaluationEndpointMapping.ToDetailDto(created, cases)
            )
        );
    }
}

/// <summary>
/// Returns one evaluation dataset with its live cases.
/// </summary>
public sealed class GetCodeReviewEvaluationDatasetHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets
) : IEndpointHandler
{
    /// <summary>Handles the get request.</summary>
    public async Task<
        Results<NotFound, ForbidHttpResult, Ok<CodeReviewEvaluationDatasetResponse>>
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string datasetId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
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

        var dataset = await datasets.FindAsync(
            organizationId,
            datasetId,
            includeArchived: false,
            cancellationToken
        );
        if (dataset is null || dataset.RepositoryId != repository.Id)
        {
            return TypedResults.NotFound();
        }

        var cases = await datasets.ListCasesAsync(
            organizationId,
            dataset.Id,
            includeRemoved: false,
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewEvaluationDatasetResponse(
                CodeReviewEvaluationEndpointMapping.ToDetailDto(dataset, cases)
            )
        );
    }
}

/// <summary>
/// Saves one complete evaluation dataset draft under an expected version.
/// </summary>
public sealed class SaveCodeReviewEvaluationDatasetHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    CodeReviewEvaluationCaptureTokenProtector captureTokens,
    CodeReviewSettings settings,
    TimeProvider timeProvider
) : IEndpointHandler
{
    /// <summary>Handles the save request.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Conflict<CodeReviewEndpointError>,
            Ok<CodeReviewEvaluationDatasetResponse>,
            JsonHttpResult<CodeReviewEndpointError>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string datasetId,
        SaveCodeReviewEvaluationDatasetRequest? request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (CodeReviewEvaluationHandlerSupport.FeatureDisabled(settings) is { } disabled)
        {
            return TypedResults.Json(disabled, statusCode: StatusCodes.Status503ServiceUnavailable);
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

        var userId = CodeReviewEvaluationHandlerSupport.UserId(user);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.NotFound();
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

        if (request is null || request.Cases is null)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("invalid_request", "A dataset draft is required.")
            );
        }

        var dataset = await datasets.FindAsync(
            organizationId,
            datasetId,
            includeArchived: false,
            cancellationToken
        );
        if (dataset is null || dataset.RepositoryId != repository.Id)
        {
            return TypedResults.NotFound();
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > CodeReviewEvaluationLimits.MaxDatasetNameLength)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    $"A dataset name is required and must be at most {CodeReviewEvaluationLimits.MaxDatasetNameLength} characters."
                )
            );
        }

        if (
            request.Description is
            { Length: > CodeReviewEvaluationLimits.MaxDatasetDescriptionLength }
        )
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    $"A dataset description must be at most {CodeReviewEvaluationLimits.MaxDatasetDescriptionLength} characters."
                )
            );
        }

        if (request.Cases.Any(item => item is null))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "Every case entry must be an object."
                )
            );
        }

        if (request.Cases.Count > CodeReviewEvaluationLimits.MaxCases)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "case_limit_reached",
                    $"A dataset can hold at most {CodeReviewEvaluationLimits.MaxCases} cases."
                )
            );
        }

        var now = timeProvider.GetUtcNow();
        var caseChanges = new List<CodeReviewEvaluationCaseSave>(request.Cases.Count);

        foreach (var item in request.Cases)
        {
            CodeReviewEvaluationStagedCapture? capture = null;

            if (!string.IsNullOrWhiteSpace(item.CaptureToken))
            {
                if (
                    !captureTokens.TryUnprotect(item.CaptureToken, out var payload)
                    || payload is null
                )
                {
                    // Distinguish an authentic-but-expired token so the browser can tell the
                    // user to re-resolve instead of losing the whole draft.
                    return captureTokens.TryUnprotectIgnoringExpiry(item.CaptureToken, out _)
                        ? TypedResults.BadRequest(
                            new CodeReviewEndpointError(
                                "capture_token_expired",
                                "The staged capture expired. Resolve the pull request again to restage it."
                            )
                        )
                        : TypedResults.BadRequest(
                            new CodeReviewEndpointError(
                                "capture_token_invalid",
                                "The staged capture token is invalid."
                            )
                        );
                }

                if (
                    !string.Equals(payload.OrganizationId, organizationId, StringComparison.Ordinal)
                    || !string.Equals(payload.RepositoryId, repository.Id, StringComparison.Ordinal)
                    || !string.Equals(payload.DatasetId, dataset.Id, StringComparison.Ordinal)
                    || payload.ExpectedDatasetVersion != request.ExpectedVersion
                    || payload.Capture is null
                )
                {
                    return TypedResults.BadRequest(
                        new CodeReviewEndpointError(
                            "capture_token_mismatch",
                            "The staged capture belongs to a different dataset or version."
                        )
                    );
                }

                if (payload.Capture.PullRequestNumber != item.PullRequestNumber)
                {
                    return TypedResults.BadRequest(
                        new CodeReviewEndpointError(
                            "capture_token_mismatch",
                            "The staged capture belongs to a different pull request."
                        )
                    );
                }

                capture = payload.Capture;
            }

            if (item.CaseId is null && capture is null)
            {
                return TypedResults.BadRequest(
                    new CodeReviewEndpointError(
                        "capture_token_invalid",
                        "New dataset membership requires a staged capture token."
                    )
                );
            }

            var referenceError = CodeReviewEvaluationHandlerSupport.ValidateReferenceSets(
                item.ReferenceSets
            );
            if (referenceError is not null)
            {
                return TypedResults.BadRequest(referenceError);
            }

            caseChanges.Add(
                new(
                    item.CaseId,
                    item.PullRequestNumber,
                    capture,
                    item.ReferenceSets is null
                        ? null
                        :
                        [
                            .. item.ReferenceSets.Select(set =>
                                CodeReviewEvaluationEndpointMapping.ToModel(set, userId, now)
                            ),
                        ]
                )
            );
        }

        var result = await datasets.SaveAsync(
            new(
                organizationId,
                repository.Id,
                dataset.Id,
                request.ExpectedVersion,
                name,
                string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                caseChanges
            ),
            cancellationToken
        );

        return result.Outcome switch
        {
            CodeReviewEvaluationDatasetSaveOutcome.Saved => TypedResults.Ok(
                new CodeReviewEvaluationDatasetResponse(
                    CodeReviewEvaluationEndpointMapping.ToDetailDto(result.Dataset!, result.Cases!)
                )
            ),
            CodeReviewEvaluationDatasetSaveOutcome.DatasetNotFound => TypedResults.NotFound(),
            CodeReviewEvaluationDatasetSaveOutcome.VersionConflict => TypedResults.Conflict(
                new CodeReviewEndpointError(
                    "version_conflict",
                    "The dataset changed since it was loaded. Reload and reapply your draft."
                )
            ),
            CodeReviewEvaluationDatasetSaveOutcome.InvalidCase => TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "The dataset draft contains an invalid case or reference set."
                )
            ),
            _ => TypedResults.BadRequest(
                new CodeReviewEndpointError("invalid_request", "The dataset draft was rejected.")
            ),
        };
    }
}

/// <summary>
/// Archives one evaluation dataset.
/// </summary>
public sealed class ArchiveCodeReviewEvaluationDatasetHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    CodeReviewSettings settings
) : IEndpointHandler
{
    /// <summary>Handles the archive request.</summary>
    public async Task<
        Results<NotFound, NoContent, ForbidHttpResult, JsonHttpResult<CodeReviewEndpointError>>
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string datasetId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (CodeReviewEvaluationHandlerSupport.FeatureDisabled(settings) is { } disabled)
        {
            return TypedResults.Json(disabled, statusCode: StatusCodes.Status503ServiceUnavailable);
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

        var dataset = await datasets.FindAsync(
            organizationId,
            datasetId,
            includeArchived: false,
            cancellationToken
        );
        if (dataset is null || dataset.RepositoryId != repository.Id)
        {
            return TypedResults.NotFound();
        }

        await datasets.ArchiveAsync(organizationId, dataset.Id, cancellationToken);

        return TypedResults.NoContent();
    }
}
