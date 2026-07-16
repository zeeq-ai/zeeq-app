namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Writes rendered Zeeq comment Markdown to GitHub.
/// </summary>
/// <remarks>
/// The writer owns GitHub mutation only. It does not render sections and does
/// not update the anchor table. Callers pass the full Markdown body after the
/// DOM renderer has preserved or replaced the appropriate sections, then persist
/// the returned GitHub comment id through <see cref="IGitHubCommentAnchorStore" />.
/// </remarks>
public interface IGitHubCommentWriter
{
    /// <summary>
    /// Creates or updates the GitHub comment for the selected target.
    /// </summary>
    /// <returns>The GitHub comment id that now owns the rendered body.</returns>
    Task<long> UpsertAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        long? existingCommentId,
        string body,
        CancellationToken cancellationToken
    );
}
