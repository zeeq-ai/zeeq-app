using System.Net;
using Zeeq.Platform.CodeReviews;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Octokit adapter for GitHub comment reaction writes.
/// </summary>
/// <remarks>
/// GitHub exposes reactions on issue comments and pull request review comments
/// through separate REST endpoints. Zeeq's feedback workflow uses one queue
/// message, so this adapter selects the endpoint from
/// <see cref="GitHubCommentReactionTarget.Kind"/> and normalizes GitHub API
/// responses into <see cref="GitHubCommentReactionWriteOutcome"/>.
/// </remarks>
internal sealed class OctokitGitHubCommentReactionClient(GitHubClient client)
    : IGitHubCommentReactionClient
{
    /// <inheritdoc />
    public async Task<GitHubCommentReactionWriteOutcome> AddReactionAsync(
        string ownerQualifiedRepoName,
        GitHubCommentReactionTarget target,
        string reactionContent,
        CancellationToken cancellationToken
    )
    {
        var (owner, repo) = RepositoryName.Parse(ownerQualifiedRepoName);
        var reaction = new NewReaction(ParseReactionType(reactionContent));
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _ = target.Kind switch
            {
                GitHubCommentReactionTargetKind.IssueComment =>
                    await client.Reaction.IssueComment.Create(
                        owner,
                        repo,
                        target.CommentId,
                        reaction
                    ),
                GitHubCommentReactionTargetKind.PullRequestReviewComment =>
                    await client.Reaction.PullRequestReviewComment.Create(
                        owner,
                        repo,
                        target.CommentId,
                        reaction
                    ),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(target),
                    target.Kind,
                    "Unsupported GitHub reaction target kind."
                ),
            };

            return GitHubCommentReactionWriteOutcome.Created;
        }
        catch (ApiException exception)
            when (exception.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return GitHubCommentReactionWriteOutcome.ValidationFailed;
        }
    }

    private static ReactionType ParseReactionType(string reactionContent) =>
        reactionContent switch
        {
            GitHubCommentReactionContent.PlusOne => ReactionType.Plus1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reactionContent),
                reactionContent,
                "Unsupported GitHub reaction content."
            ),
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
