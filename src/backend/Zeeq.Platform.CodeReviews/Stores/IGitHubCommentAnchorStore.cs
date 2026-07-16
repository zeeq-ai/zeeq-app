using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for durable GitHub comment target anchors.
/// </summary>
/// <remarks>
/// Anchors map Zeeq's logical render target to GitHub's numeric comment id.
/// They let the normal writer path fetch and update the known comment directly
/// instead of scanning every PR comment. The data remains repairable: if GitHub
/// returns 404 or the row is missing, the resolver can scan for Zeeq markers
/// and write the recovered id back through this store.
///
/// This interface is intentionally narrow and portable. It belongs to the code
/// review slice today, but the concept can move to a shared GitHub integration
/// home if more features start rendering owned GitHub comments.
/// </remarks>
public interface IGitHubCommentAnchorStore
{
    /// <summary>
    /// Finds the anchor for one logical comment target.
    /// </summary>
    Task<GitHubCommentAnchor?> FindAsync(
        GitHubCommentTargetSelector target,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates or updates the resolved GitHub comment id for a target.
    /// </summary>
    /// <remarks>
    /// Callers use this after creating a comment or after recovering a stale or
    /// missing anchor through marker scanning. The owner-qualified repository
    /// name is stored with the anchor so operational/debug paths can understand
    /// the external GitHub location without joining back through PR state.
    /// </remarks>
    Task<GitHubCommentAnchor> UpsertResolvedAsync(
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        long gitHubCommentId,
        CancellationToken cancellationToken
    );
}
