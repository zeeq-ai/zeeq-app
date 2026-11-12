using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Browses known pull requests for dataset curation.
/// </summary>
public sealed class ListCodeReviewEvaluationPrTargetsHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    CodeReviewEvaluationPrTargetService targetService
) : IEndpointHandler
{
    private const int MaxPageSize = 100;

    /// <summary>Handles the browse request.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewEvaluationPrTargetListResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string? query,
        string? datasetId,
        string? cursor,
        int? pageSize,
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

        if (pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "Page size must be between 1 and 100."
                )
            );
        }

        if (!TryDecodeCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("invalid_request", "The cursor is malformed.")
            );
        }

        var members = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(datasetId))
        {
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

            // Active membership only: a removed PR must stay selectable to be re-added.
            var cases = await datasets.ListCasesAsync(
                organizationId,
                dataset.Id,
                includeRemoved: false,
                cancellationToken
            );
            members.UnionWith(cases.Select(item => item.PullRequestNumber));
        }

        var page = await targetService.BrowseAsync(
            new(organizationId, repository.Id, query, cursorCreatedAtUtc, cursorId, pageSize ?? 50),
            members,
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewEvaluationPrTargetListResponse(
                [
                    .. page.Items.Select(item =>
                        CodeReviewEvaluationEndpointMapping.ToDto(item, members)
                    ),
                ],
                EncodeCursor(page)
            )
        );
    }

    private static string? EncodeCursor(CodeReviewEvaluationPrTargetPage page) =>
        page.NextCursorCreatedAtUtc is null || page.NextCursorId is null
            ? null
            : $"{page.NextCursorCreatedAtUtc.Value.UtcTicks}|{page.NextCursorId}";

    private static bool TryDecodeCursor(
        string? cursor,
        out DateTimeOffset cursorCreatedAtUtc,
        out string? cursorId
    )
    {
        cursorCreatedAtUtc = default;
        cursorId = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        var parts = cursor.Split('|', 2);
        if (
            parts.Length != 2
            || !long.TryParse(parts[0], out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks
        )
        {
            return false;
        }

        cursorCreatedAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        cursorId = parts[1];
        return !string.IsNullOrWhiteSpace(cursorId);
    }
}

/// <summary>
/// Resolves and stages manual pull request entries.
/// </summary>
public sealed class ResolveCodeReviewEvaluationPrTargetsHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    CodeReviewEvaluationPrTargetService targetService,
    CodeReviewSettings settings
) : IEndpointHandler
{
    /// <summary>Handles the resolve request.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<ResolveCodeReviewEvaluationPrTargetsResponse>,
            JsonHttpResult<CodeReviewEndpointError>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        ResolveCodeReviewEvaluationPrTargetsRequest? request,
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

        if (
            request is null
            || string.IsNullOrWhiteSpace(request.DatasetId)
            || request.Entries is null
            || request.Entries.Count == 0
        )
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    "A dataset id and at least one pull request entry are required."
                )
            );
        }

        var dataset = await datasets.FindAsync(
            organizationId,
            request.DatasetId,
            includeArchived: false,
            cancellationToken
        );
        if (dataset is null || dataset.RepositoryId != repository.Id)
        {
            return TypedResults.NotFound();
        }

        var resolutions = await targetService.ResolveAsync(
            organizationId,
            repository.Id,
            repository.OwnerQualifiedName,
            dataset.Id,
            request.ExpectedDatasetVersion,
            request.Entries,
            cancellationToken
        );

        var members = new HashSet<int>(
            (
                await datasets.ListCasesAsync(
                    organizationId,
                    dataset.Id,
                    includeRemoved: false,
                    cancellationToken
                )
            ).Select(item => item.PullRequestNumber)
        );

        return TypedResults.Ok(
            new ResolveCodeReviewEvaluationPrTargetsResponse([
                .. resolutions.Select(item => new CodeReviewEvaluationPrResolutionDto(
                    item.Entry,
                    item.PullRequestNumber,
                    item.Target is null
                        ? null
                        : CodeReviewEvaluationEndpointMapping.ToDto(item.Target, members),
                    item.Capture?.CaptureToken,
                    item.Capture?.ExpiresAtUtc,
                    item.Capture is null
                        ? null
                        : CodeReviewEvaluationEndpointMapping.ToPreview(
                            item.Capture.Capture,
                            item.PullRequestNumber ?? 0
                        ),
                    item.ErrorCode,
                    item.ErrorMessage
                )),
            ])
        );
    }
}

/// <summary>
/// Stages a fresh capture for one existing dataset case.
/// </summary>
public sealed class RefreshCodeReviewEvaluationCaseHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    CodeReviewEvaluationCaptureService captures,
    CodeReviewSettings settings
) : IEndpointHandler
{
    /// <summary>Handles the refresh request.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<RefreshCodeReviewEvaluationCaseResponse>,
            JsonHttpResult<CodeReviewEndpointError>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string datasetId,
        string caseId,
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

        var item = await datasets.FindCaseAsync(
            organizationId,
            dataset.Id,
            caseId,
            includeRemoved: true,
            cancellationToken
        );
        if (item is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            var outcome = await captures.CaptureAsync(
                organizationId,
                repository.Id,
                repository.OwnerQualifiedName,
                dataset.Id,
                dataset.Version,
                item.PullRequestNumber,
                cancellationToken
            );

            return TypedResults.Ok(
                new RefreshCodeReviewEvaluationCaseResponse(
                    outcome.CaptureToken,
                    outcome.ExpiresAtUtc,
                    CodeReviewEvaluationEndpointMapping.ToPreview(
                        outcome.Capture,
                        item.PullRequestNumber
                    )
                )
            );
        }
        catch (CodeReviewEvaluationCaptureException exception)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(exception.Code, exception.Message)
            );
        }
    }
}
