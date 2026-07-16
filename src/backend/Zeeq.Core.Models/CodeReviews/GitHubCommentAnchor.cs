namespace Zeeq.Core.Models;

/// <summary>
/// Durable pointer from one Zeeq comment target to the GitHub comment id.
/// </summary>
/// <remarks>
/// The anchor exists so repeated render signals can update the same GitHub
/// comment without scanning every PR comment on the normal path. It is not the
/// comment document and does not store rendered Markdown. The live GitHub
/// comment body remains the document state; this row only remembers where that
/// document currently lives in GitHub.
///
/// The row is deliberately repairable. If the stored GitHub id is stale or
/// missing, the resolver can scan GitHub for Zeeq's root marker, recover the
/// id, and update this row. That keeps this table useful for performance while
/// avoiding a hard dependency on it for correctness.
/// </remarks>
public sealed class GitHubCommentAnchor : IOrganizationScopedEntity, IUpdatedEntity
{
    /// <summary>
    /// Stable target key generated from organization, repository, PR number, kind, and scope.
    /// </summary>
    public required string TargetKey { get; init; }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Local repository mapping id.
    /// </summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Provider-qualified repository name, for example <c>owner/repo</c>.
    /// </summary>
    public required string OwnerQualifiedRepoName { get; set; }

    /// <summary>
    /// Provider pull request number that owns the comment target.
    /// </summary>
    public int PullRequestNumber { get; init; }

    /// <summary>
    /// Logical comment surface within the pull request.
    /// </summary>
    public GitHubCommentTargetKind Kind { get; init; }

    /// <summary>
    /// Target-specific scope key, such as <c>root</c> or a review-thread id.
    /// </summary>
    public required string ScopeKey { get; init; }

    /// <summary>
    /// GitHub's numeric comment id when Zeeq has resolved or created the comment.
    /// </summary>
    public long? GitHubCommentId { get; set; }

    /// <summary>
    /// Last time Zeeq resolved this anchor against GitHub.
    /// </summary>
    public DateTimeOffset? LastResolvedAtUtc { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
