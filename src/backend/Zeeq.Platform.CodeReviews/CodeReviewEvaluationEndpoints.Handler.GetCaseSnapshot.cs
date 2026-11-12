using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Returns the saved capture for one evaluation dataset case.
/// </summary>
/// <remarks>
/// This exists so the dataset diff view renders stored code without a live provider call. The
/// artifact reference is read from the server-side case row; no caller-supplied URI is accepted.
/// Authorization runs before any child lookup, and a mismatched repository or dataset returns
/// 404 rather than revealing that a case exists elsewhere.
/// </remarks>
public sealed class GetCodeReviewEvaluationCaseSnapshotHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    ICodeReviewEvaluationDatasetStore datasets,
    ICodeReviewEvaluationArtifactStore artifacts
) : IEndpointHandler
{
    /// <summary>Handles the snapshot read request.</summary>
    public async Task<
        Results<NotFound, ForbidHttpResult, Ok<CodeReviewEvaluationCaseSnapshotResponse>>
    > HandleAsync(
        string organizationId,
        string repositoryId,
        string datasetId,
        string caseId,
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
            includeArchived: true,
            cancellationToken
        );
        if (dataset is null || dataset.RepositoryId != repository.Id)
        {
            return TypedResults.NotFound();
        }

        // Removed membership is included on purpose: a diff of a case that was later removed
        // must still render its captured code.
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

        var snapshot = await artifacts.ReadSnapshotAsync(
            organizationId,
            repository.Id,
            new CodeReviewEvaluationArtifact(
                item.SnapshotStorageUri,
                item.SnapshotSha256,
                item.SnapshotByteCount
            ),
            cancellationToken
        );

        return TypedResults.Ok(new CodeReviewEvaluationCaseSnapshotResponse(snapshot));
    }
}
