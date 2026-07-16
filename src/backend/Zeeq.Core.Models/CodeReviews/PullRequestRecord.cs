namespace Zeeq.Core.Models;

/// <summary>
/// Durable pull request state for webhook-driven review and UI streams.
/// </summary>
/// <remarks>
/// NOTE: This entity maps to the partitioned <c>code_review_pull_request_records</c>
/// table. The table is range-partitioned by <see cref="DomainEntityBase.CreatedAtUtc" />
/// and uses <c>(id, created_at_utc)</c> as its primary key because PostgreSQL requires
/// partition keys to participate in unique constraints. The pg_partman extension manages
/// child partition creation and retention metadata for this table. Future migrations that
/// add columns, constraints, indexes, or foreign keys must account for the partitioned
/// parent and existing child partitions; do not assume a normal EF table migration is
/// sufficient. Cross-partition uniqueness for provider PR identity belongs in
/// <see cref="PullRequestLookup" />, not on this table.
/// </remarks>
public sealed class PullRequestRecord : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Optional Zeeq team context for this pull request.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Local repository mapping ID.
    /// </summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Provider-qualified repository name, for example <c>owner/repo</c>.
    /// </summary>
    public required string OwnerQualifiedRepoName { get; set; }

    /// <summary>
    /// Provider pull request number.
    /// </summary>
    public int PullRequestNumber { get; init; }

    /// <summary>
    /// GitHub GraphQL node ID for the pull request.
    /// </summary>
    public required string GitHubNodeId { get; set; }

    /// <summary>
    /// Pull request title.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Source branch name.
    /// </summary>
    public required string Branch { get; set; }

    /// <summary>
    /// Target branch name.
    /// </summary>
    public required string BaseBranch { get; set; }

    /// <summary>
    /// Current head commit SHA.
    /// </summary>
    public required string HeadSha { get; set; }

    /// <summary>
    /// Provider login of the pull request author.
    /// </summary>
    public required string AuthorLogin { get; set; }

    /// <summary>
    /// Browser URL for the pull request.
    /// </summary>
    public required string HtmlUrl { get; set; }

    /// <summary>
    /// True when the pull request is currently a draft.
    /// </summary>
    public bool IsDraft { get; set; }

    /// <summary>
    /// Provider pull request state.
    /// </summary>
    public PullRequestState State { get; set; }

    /// <summary>
    /// Zeeq claim state for assignment workflows.
    /// </summary>
    public PullRequestClaimStatus ClaimStatus { get; set; }

    /// <summary>
    /// User currently claiming this pull request, when any.
    /// </summary>
    public string? ClaimedByUserId { get; set; }

    /// <summary>
    /// Optional feature id associated with this pull request.
    /// </summary>
    public string? FeatureId { get; set; }

    /// <summary>
    /// Serialized Zeeq tags.
    /// </summary>
    public string TagsJson { get; set; } = "[]";

    /// <summary>
    /// Serialized provider labels.
    /// </summary>
    public string LabelsJson { get; set; } = "[]";

    /// <summary>
    /// First webhook time observed by Zeeq.
    /// </summary>
    public DateTimeOffset CreatedFromWebhookAtUtc { get; init; }

    /// <summary>
    /// Most recent webhook time observed by Zeeq.
    /// </summary>
    public DateTimeOffset LastWebhookAtUtc { get; set; }

    /// <summary>
    /// Typed check-run gating state, stored as a nullable JSONB document.
    /// </summary>
    /// <remarks>
    /// Null when the repository has no check-run gating configured or no check
    /// has been posted for this PR. The state carries the GitHub check-run id
    /// used for subsequent Update API calls (resolve, bypass) and the bypass
    /// audit trail.
    /// </remarks>
    public PullRequestCheckRunState? CheckRunState { get; set; }
}
