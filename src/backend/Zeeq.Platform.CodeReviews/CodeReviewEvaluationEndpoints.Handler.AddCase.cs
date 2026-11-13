using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Adds one captured pull request while preserving the complete dataset draft.</summary>
public sealed class AddCodeReviewEvaluationCaseHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    CodeReviewEvaluationCaptureService captures,
    CodeReviewSettings settings
) : IEndpointHandler
{
    /// <summary>Handles a retry-safe add with a single compare-and-swap attempt.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Conflict<CodeReviewEndpointError>,
            Ok<AddCodeReviewEvaluationCaseResponse>,
            JsonHttpResult<CodeReviewEndpointError>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string datasetId,
        AddCodeReviewEvaluationCaseRequest? request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (CodeReviewEvaluationHandlerSupport.FeatureDisabled(settings) is { } disabled)
        {
            return TypedResults.Json(disabled, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var access = await authorization.ResolveAsync(organizationId, user, cancellationToken);
        if (
            access is null
            || string.IsNullOrWhiteSpace(CodeReviewEvaluationHandlerSupport.UserId(user))
        )
        {
            return TypedResults.NotFound();
        }
        if (!access.CanManage)
        {
            return TypedResults.Forbid();
        }
        if (request is null || request.PullRequestNumber <= 0 || request.ExpectedVersion <= 0)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "A positive pull request number and expected version are required."
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
        var existing = cases.FirstOrDefault(item =>
            item.PullRequestNumber == request.PullRequestNumber
        );
        if (existing is not null)
        {
            return TypedResults.Ok(Existing(dataset, existing));
        }
        if (dataset.Version != request.ExpectedVersion)
        {
            return Conflict();
        }
        if (cases.Count >= CodeReviewEvaluationLimits.MaxCases)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "case_limit_reached",
                    $"A dataset can hold at most {CodeReviewEvaluationLimits.MaxCases} cases."
                )
            );
        }

        CodeReviewEvaluationCaptureOutcome outcome;
        try
        {
            outcome = await captures.CaptureAsync(
                organizationId,
                repository.Id,
                repository.OwnerQualifiedName,
                dataset.Id,
                request.ExpectedVersion,
                request.PullRequestNumber,
                cancellationToken
            );
        }
        catch (CodeReviewEvaluationCaptureException exception)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(exception.Code, exception.Message)
            );
        }

        var changes = cases
            .Select(item => new CodeReviewEvaluationCaseSave(
                item.Id,
                item.PullRequestNumber,
                null,
                null
            ))
            .ToList();
        changes.Add(new(null, request.PullRequestNumber, outcome.Capture, null));
        var result = await datasets.SaveAsync(
            new(
                organizationId,
                repository.Id,
                dataset.Id,
                request.ExpectedVersion,
                dataset.Name,
                dataset.Description,
                changes
            ),
            cancellationToken
        );
        if (result.Outcome == CodeReviewEvaluationDatasetSaveOutcome.Saved)
        {
            var capture = outcome.Capture;
            return TypedResults.Ok(
                new AddCodeReviewEvaluationCaseResponse(
                    true,
                    dataset.Id,
                    result.Dataset!.Version,
                    request.PullRequestNumber,
                    capture.BaseSha,
                    capture.HeadSha,
                    capture.CapturedAtUtc
                )
            );
        }
        if (result.Outcome == CodeReviewEvaluationDatasetSaveOutcome.VersionConflict)
        {
            // A concurrent add of the same PR is a no-op; never replay the write.
            var current = await datasets.FindAsync(
                organizationId,
                dataset.Id,
                includeArchived: false,
                cancellationToken
            );
            if (current is null || current.RepositoryId != repository.Id)
            {
                return TypedResults.NotFound();
            }
            var currentCases = await datasets.ListCasesAsync(
                organizationId,
                dataset.Id,
                includeRemoved: false,
                cancellationToken
            );
            var winner = currentCases.FirstOrDefault(item =>
                item.PullRequestNumber == request.PullRequestNumber
            );
            return winner is null ? Conflict() : TypedResults.Ok(Existing(current, winner));
        }
        if (result.Outcome == CodeReviewEvaluationDatasetSaveOutcome.DatasetNotFound)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.BadRequest(
            new CodeReviewEndpointError("invalid_request", "The dataset case was rejected.")
        );
    }

    private static Conflict<CodeReviewEndpointError> Conflict() =>
        TypedResults.Conflict(
            new CodeReviewEndpointError(
                "version_conflict",
                "The dataset changed since it was loaded. Reload and try again."
            )
        );

    private static AddCodeReviewEvaluationCaseResponse Existing(
        CodeReviewEvaluationDataset dataset,
        CodeReviewEvaluationCase item
    ) =>
        new(
            false,
            dataset.Id,
            dataset.Version,
            item.PullRequestNumber,
            item.BaseSha,
            item.HeadSha,
            item.CapturedAtUtc
        );
}
