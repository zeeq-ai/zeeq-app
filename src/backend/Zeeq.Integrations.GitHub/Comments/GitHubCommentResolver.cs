using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Resolves existing Zeeq-owned GitHub comments for the DOM renderer.
/// </summary>
/// <remarks>
/// The anchor table gives us the fast path, but GitHub remains the source of
/// truth for the rendered document. This resolver first tries the stored comment
/// id and then repairs stale or missing anchors by scanning for the target root
/// marker. Keeping that logic here prevents handlers from needing to know which
/// GitHub API family backs each target kind.
/// </remarks>
internal sealed class GitHubCommentResolver : IGitHubCommentResolver
{
    /// <summary>
    /// Resolves the current GitHub comment body and parses it into a DOM.
    /// </summary>
    public async Task<GitHubCommentResolution?> ResolveAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        long? storedCommentId,
        CancellationToken cancellationToken
    )
    {
        if (storedCommentId is not null)
        {
            var comment = await GetByStoredIdAsync(
                client,
                target.Kind,
                ownerQualifiedRepoName,
                storedCommentId.Value,
                cancellationToken
            );

            if (
                comment is not null
                && GitHubCommentDomParser.ContainsRootForTarget(target, comment.Body)
            )
            {
                return new(comment.CommentId, GitHubCommentDomParser.Parse(target, comment.Body));
            }

            // Missing, deleted, or wrong-target comments fall through to the
            // scan path. The caller can then repair the anchor with the id we
            // recover from GitHub.
        }

        await foreach (
            var comment in EnumerateCandidatesAsync(
                client,
                target,
                ownerQualifiedRepoName,
                cancellationToken
            )
        )
        {
            if (!GitHubCommentDomParser.ContainsRootForTarget(target, comment.Body))
            {
                continue;
            }

            return new(comment.CommentId, GitHubCommentDomParser.Parse(target, comment.Body));
        }

        return null;
    }

    private static Task<GitHubCommentCandidate?> GetByStoredIdAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetKind kind,
        string ownerQualifiedRepoName,
        long commentId,
        CancellationToken cancellationToken
    ) =>
        IsIssueCommentTarget(kind)
            ? client.GetIssueCommentAsync(ownerQualifiedRepoName, commentId, cancellationToken)
            : client.GetPullRequestReviewCommentAsync(
                ownerQualifiedRepoName,
                commentId,
                cancellationToken
            );

    private static IAsyncEnumerable<GitHubCommentCandidate> EnumerateCandidatesAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        CancellationToken cancellationToken
    ) =>
        IsIssueCommentTarget(target.Kind)
            ? client.EnumerateIssueCommentsAsync(
                ownerQualifiedRepoName,
                target.PullRequestNumber,
                cancellationToken
            )
            : client.EnumeratePullRequestReviewCommentsAsync(
                ownerQualifiedRepoName,
                target.PullRequestNumber,
                cancellationToken
            );

    private static bool IsIssueCommentTarget(GitHubCommentTargetKind kind) =>
        kind
            is GitHubCommentTargetKind.PullRequestSummary
                or GitHubCommentTargetKind.StandaloneIssueComment;
}
