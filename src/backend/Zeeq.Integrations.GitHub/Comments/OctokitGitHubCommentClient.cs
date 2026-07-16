using System.Runtime.CompilerServices;
using Zeeq.Platform.CodeReviews;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Octokit adapter for the small GitHub comment API surface used by Zeeq.
/// </summary>
/// <remarks>
/// Octokit exposes issue comments and pull request review comments through
/// different clients. Zeeq's renderer works with logical targets instead, so
/// this adapter translates those GitHub response types into
/// <see cref="GitHubCommentCandidate" /> and hides pagination details from the
/// resolver and writer.
/// </remarks>
public sealed class OctokitGitHubCommentClient(GitHubClient client) : IGitHubCommentClient
{
    private const int PageSize = 100;

    /// <summary>
    /// Gets an issue comment directly by id.
    /// </summary>
    public async Task<GitHubCommentCandidate?> GetIssueCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var comment = await client.Issue.Comment.Get(owner, repo, commentId);
            return new(comment.Id, comment.Body ?? string.Empty);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets a pull request review comment directly by id.
    /// </summary>
    public async Task<GitHubCommentCandidate?> GetPullRequestReviewCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var comment = await client.PullRequest.ReviewComment.GetComment(owner, repo, commentId);
            return new(comment.Id, comment.Body ?? string.Empty);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pages through issue comments on the pull request issue timeline.
    /// </summary>
    public async IAsyncEnumerable<GitHubCommentCandidate> EnumerateIssueCommentsAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comments = await client.Issue.Comment.GetAllForIssue(
                owner,
                repo,
                pullRequestNumber,
                OnePage(page)
            );

            foreach (var comment in comments)
            {
                yield return new(comment.Id, comment.Body ?? string.Empty);
            }

            if (comments.Count < PageSize)
            {
                yield break;
            }

            page++;
        }
    }

    /// <summary>
    /// Pages through pull request review comments.
    /// </summary>
    public async IAsyncEnumerable<GitHubCommentCandidate> EnumeratePullRequestReviewCommentsAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comments = await client.PullRequest.ReviewComment.GetAll(
                owner,
                repo,
                pullRequestNumber,
                OnePage(page)
            );

            foreach (var comment in comments)
            {
                yield return new(comment.Id, comment.Body ?? string.Empty);
            }

            if (comments.Count < PageSize)
            {
                yield break;
            }

            page++;
        }
    }

    /// <summary>
    /// Creates an issue comment on the PR timeline.
    /// </summary>
    public async Task<long> CreateIssueCommentAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string body,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        cancellationToken.ThrowIfCancellationRequested();

        var comment = await client.Issue.Comment.Create(owner, repo, pullRequestNumber, body);
        return comment.Id;
    }

    /// <summary>
    /// Updates an issue comment by id.
    /// </summary>
    public async Task<long> UpdateIssueCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        string body,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var comment = await client.Issue.Comment.Update(owner, repo, commentId, body);
            return comment.Id;
        }
        catch (NotFoundException exception)
        {
            throw new GitHubCommentNotFoundException(commentId, exception);
        }
    }

    /// <summary>
    /// Updates a pull request review comment by id.
    /// </summary>
    public async Task<long> UpdatePullRequestReviewCommentAsync(
        string ownerQualifiedRepoName,
        long commentId,
        string body,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var comment = await client.PullRequest.ReviewComment.Edit(
                owner,
                repo,
                commentId,
                new PullRequestReviewCommentEdit(body)
            );
            return comment.Id;
        }
        catch (NotFoundException exception)
        {
            throw new GitHubCommentNotFoundException(commentId, exception);
        }
    }

    /// <summary>
    /// Creates a reply in an existing pull request review thread.
    /// </summary>
    public async Task<long> CreatePullRequestReviewReplyAsync(
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        long parentCommentId,
        string body,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        cancellationToken.ThrowIfCancellationRequested();

        var comment = await client.PullRequest.ReviewComment.CreateReply(
            owner,
            repo,
            pullRequestNumber,
            new PullRequestReviewCommentReplyCreate(body, parentCommentId)
        );
        return comment.Id;
    }

    private static ApiOptions OnePage(int page) =>
        new()
        {
            PageSize = PageSize,
            PageCount = 1,
            StartPage = page,
        };

    private readonly record struct RepositoryName(string Owner, string Name)
    {
        public static RepositoryName Parse(string ownerQualifiedRepoName)
        {
            var separator = ownerQualifiedRepoName.IndexOf('/', StringComparison.Ordinal);
            if (separator <= 0 || separator == ownerQualifiedRepoName.Length - 1)
            {
                throw new ArgumentException(
                    "Repository name must be in owner/repo form.",
                    nameof(ownerQualifiedRepoName)
                );
            }

            return new(
                ownerQualifiedRepoName[..separator],
                ownerQualifiedRepoName[(separator + 1)..]
            );
        }
    }
}

/// <summary>
/// Raised when GitHub no longer has a comment that Zeeq attempted to update.
/// </summary>
/// <remarks>
/// The writer catches this exception on the direct-id update path and falls back
/// to marker scanning. That preserves the repairable-anchor behavior: stale ids
/// are not fatal as long as the Zeeq marker still exists somewhere on GitHub.
/// </remarks>
public sealed class GitHubCommentNotFoundException(long commentId, Exception innerException)
    : InvalidOperationException($"GitHub comment {commentId} was not found.", innerException)
{
    /// <summary>
    /// GitHub comment id that could not be updated.
    /// </summary>
    public long CommentId { get; } = commentId;
}
