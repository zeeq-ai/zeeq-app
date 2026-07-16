namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider adapter used by the reaction queue handler to mutate GitHub reactions.
/// </summary>
/// <remarks>
/// The platform handler owns queue semantics and retry/no-op decisions, but it
/// should not know Octokit or GitHub App authentication details. The GitHub
/// integration layer implements this interface with an installation-authenticated
/// client and translates GitHub status codes into
/// <see cref="GitHubCommentReactionWriteOutcome" />.
/// </remarks>
public interface IGitHubCommentReactionClient
{
    /// <summary>
    /// Adds the requested reaction to the target GitHub comment.
    /// </summary>
    /// <param name="ownerQualifiedRepoName">Repository name in <c>owner/repo</c> form.</param>
    /// <param name="target">Comment surface and id to receive the reaction.</param>
    /// <param name="reactionContent">GitHub reaction content such as <c>+1</c>.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The normalized GitHub reaction write outcome.</returns>
    Task<GitHubCommentReactionWriteOutcome> AddReactionAsync(
        string ownerQualifiedRepoName,
        GitHubCommentReactionTarget target,
        string reactionContent,
        CancellationToken cancellationToken
    );
}
