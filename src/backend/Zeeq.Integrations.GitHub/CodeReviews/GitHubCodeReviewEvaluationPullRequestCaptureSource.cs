using System.Text;
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

                var files = await FetchFilesWithinBudgetAsync(
                    (page, pageToken) =>
                        client.PullRequest.Files(
                            owner,
                            repository,
                            request.PullRequestNumber,
                            new ApiOptions
                            {
                                StartPage = page,
                                PageCount = 1,
                                PageSize = FilePageSize,
                            }
                        ),
                    MeasureFileBytes,
                    cancellationToken
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

    /// <summary>Files requested per Octokit page.</summary>
    internal const int FilePageSize = 100;

    /// <summary>
    /// Fetches changed files page by page and stops as soon as the accumulated patch/path bytes
    /// exceed the evaluation snapshot budget.
    /// </summary>
    /// <remarks>
    /// The persisted snapshot limit is exact and includes envelope metadata; this pre-check is a
    /// memory guard so a huge pull request never materializes every patch. Exceeding it is
    /// reported as `snapshot_too_large`, and later pages are never requested. Head/base
    /// stability is verified by the caller around this fetch.
    /// </remarks>
    internal static async Task<IReadOnlyList<T>> FetchFilesWithinBudgetAsync<T>(
        Func<int, CancellationToken, Task<IReadOnlyList<T>>> fetchPage,
        Func<T, long> measureBytes,
        CancellationToken cancellationToken
    )
    {
        var files = new List<T>();
        long accumulatedBytes = 0;

        for (var page = 1; ; page++)
        {
            // Octokit's Files overload does not take a token, so stop between pages.
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await fetchPage(page, cancellationToken);

            foreach (var file in batch)
            {
                accumulatedBytes += measureBytes(file);

                if (accumulatedBytes > CodeReviewEvaluationLimits.MaxSnapshotBytes)
                {
                    throw new CodeReviewEvaluationCaptureException(
                        "snapshot_too_large",
                        "The pull request exceeds the 5 MiB evaluation snapshot limit."
                    );
                }

                files.Add(file);
            }

            if (batch.Count < FilePageSize)
            {
                break;
            }
        }

        return files;
    }

    /// <summary>Bytes contributed by one file's path metadata and patch text.</summary>
    internal static long MeasureFileBytes(PullRequestFile file) =>
        Encoding.UTF8.GetByteCount(file.FileName ?? string.Empty)
        + Encoding.UTF8.GetByteCount(file.PreviousFileName ?? string.Empty)
        + Encoding.UTF8.GetByteCount(file.Patch ?? string.Empty);

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
