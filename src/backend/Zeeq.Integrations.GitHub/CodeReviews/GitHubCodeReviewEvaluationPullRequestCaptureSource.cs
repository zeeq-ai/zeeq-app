using Octokit;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// GitHub implementation of the evaluation pull request capture source.
/// </summary>
/// <remarks>
/// Capture is read-only. It never creates pull request, review, comment or check-run rows and
/// never mutates the repository. Provider metadata is fetched twice so a base or head change
/// during capture is detected instead of producing a mixed snapshot; one retry is allowed and
/// a second mismatch is reported as <c>pull_request_changed</c>.
///
/// Developer feedback comments and previous generated reviews are never fetched here, so they
/// cannot leak into an evaluation prompt or snapshot hash.
/// </remarks>
internal sealed class GitHubCodeReviewEvaluationPullRequestCaptureSource(
    IGitHubClientFactory clientFactory,
    TimeProvider timeProvider
) : ICodeReviewEvaluationProviderCaptureSource
{
    /// <inheritdoc />
    public async Task<CodeReviewEvaluationSnapshot> CaptureAsync(
        CodeReviewEvaluationCaptureRequest request,
        CancellationToken cancellationToken
    )
    {
        var (owner, repository) = GitHubRepositoryName.Parse(request.OwnerQualifiedRepoName);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var client = await clientFactory.CreateInstallationClientForOrganizationAsync(
                    request.OrganizationId,
                    cancellationToken
                );

                var before = await client.PullRequest.Get(
                    owner,
                    repository,
                    request.PullRequestNumber
                );

                var files = await client.PullRequest.Files(
                    owner,
                    repository,
                    request.PullRequestNumber
                );

                // Re-read metadata after the file list so a push during capture is visible.
                var after = await client.PullRequest.Get(
                    owner,
                    repository,
                    request.PullRequestNumber
                );

                if (
                    !string.Equals(before.Head.Sha, after.Head.Sha, StringComparison.Ordinal)
                    || !string.Equals(before.Base.Sha, after.Base.Sha, StringComparison.Ordinal)
                )
                {
                    continue;
                }

                return BuildSnapshot(request, after, files);
            }
            catch (NotFoundException)
            {
                throw new CodeReviewEvaluationCaptureException(
                    "pr_not_found",
                    "The pull request was not found or is not visible to the Zeeq GitHub installation."
                );
            }
            catch (AuthorizationException)
            {
                throw new CodeReviewEvaluationCaptureException(
                    "pr_no_access",
                    "Zeeq is not authorized to read this pull request."
                );
            }
            catch (ForbiddenException)
            {
                throw new CodeReviewEvaluationCaptureException(
                    "pr_no_access",
                    "Zeeq is not authorized to read this pull request."
                );
            }
        }

        throw new CodeReviewEvaluationCaptureException(
            "pull_request_changed",
            "The pull request changed while it was being captured. Try again."
        );
    }

    private CodeReviewEvaluationSnapshot BuildSnapshot(
        CodeReviewEvaluationCaptureRequest request,
        PullRequest pullRequest,
        IReadOnlyList<PullRequestFile> files
    )
    {
        var changedFiles = files
            .Select(file => new CodeReviewEvaluationSnapshotFile(
                file.FileName ?? string.Empty,
                file.PreviousFileName,
                MapMutationState(file.Status),
                file.Patch ?? string.Empty,
                IsOmitted: string.IsNullOrEmpty(file.Patch)
            ))
            .ToArray();

        return new(
            CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
            request.OrganizationId,
            request.RepositoryId,
            request.OwnerQualifiedRepoName,
            request.PullRequestNumber,
            pullRequest.Title ?? string.Empty,
            pullRequest.Body ?? string.Empty,
            pullRequest.Base.Sha,
            pullRequest.Head.Sha,
            pullRequest.User?.Login ?? string.Empty,
            pullRequest.HtmlUrl ?? string.Empty,
            pullRequest.Draft,
            MapState(pullRequest),
            timeProvider.GetUtcNow(),
            pullRequest.ChangedFiles,
            changedFiles
        );
    }

    private static PullRequestState MapState(PullRequest pullRequest) =>
        pullRequest.State == ItemState.Open ? PullRequestState.Open
        : pullRequest.Merged == true ? PullRequestState.Merged
        : PullRequestState.Closed;

    private static CodeReviewFileMutationState MapMutationState(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "added" => CodeReviewFileMutationState.Added,
            "removed" or "deleted" => CodeReviewFileMutationState.Deleted,
            "renamed" => CodeReviewFileMutationState.Renamed,
            "copied" => CodeReviewFileMutationState.Copied,
            "binary" => CodeReviewFileMutationState.Binary,
            _ => CodeReviewFileMutationState.Modified,
        };
}
