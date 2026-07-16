namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Minimal GitHub comment API surface required by Zeeq's comment pipeline.
/// </summary>
/// <remarks>
/// The resolver and writer need to work with both issue comments and pull
/// request review comments, but they should not depend on Octokit response
/// types. This interface is the seam that lets integration tests use a small
/// fake client while production code adapts Octokit.
/// </remarks>
public interface IGitHubCommentClient
{
    /// <summary>
    /// Gets a PR timeline or standalone issue comment by GitHub comment id.
    /// </summary>
    Task<GitHubCommentCandidate?> GetIssueCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Gets a pull request review comment by GitHub comment id.
    /// </summary>
    Task<GitHubCommentCandidate?> GetPullRequestReviewCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerates issue comments for a pull request issue timeline.
    /// </summary>
    IAsyncEnumerable<GitHubCommentCandidate> EnumerateIssueCommentsAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Enumerates pull request review comments for a pull request.
    /// </summary>
    IAsyncEnumerable<GitHubCommentCandidate> EnumeratePullRequestReviewCommentsAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a PR timeline or standalone issue comment.
    /// </summary>
    Task<long> CreateIssueCommentAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string body,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Updates a PR timeline or standalone issue comment.
    /// </summary>
    Task<long> UpdateIssueCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        string body,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Updates a pull request review comment.
    /// </summary>
    Task<long> UpdatePullRequestReviewCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        string body,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates a reply in an existing pull request review thread.
    /// </summary>
    Task<long> CreatePullRequestReviewReplyAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        long parentCommentId,
        string body,
        CancellationToken cancellationToken
    );
}
