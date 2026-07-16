using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Stable logical identity for one Zeeq-owned GitHub comment target.
/// </summary>
/// <remarks>
/// The selector answers "which GitHub comment should this render update?" It is
/// intentionally separate from DOM section ordering, which answers where a
/// section belongs inside the comment body. The storage key is used by later
/// phases for the lease row and comment anchor row.
/// </remarks>
public sealed record GitHubCommentTargetSelector(
    string OrganizationId,
    string RepositoryId,
    int PullRequestNumber,
    GitHubCommentTargetKind Kind,
    string ScopeKey
)
{
    /// <summary>
    /// Creates a stable storage key for leases and GitHub comment anchors.
    /// </summary>
    /// <returns>
    /// A key in the shape <c>{organization}:{repository}:{pullRequest}:{kind}:{scope}</c>.
    /// </returns>
    public string ToStorageKey() =>
        $"{OrganizationId}:{RepositoryId}:{PullRequestNumber}:{(int)Kind}:{ScopeKey}";
}
