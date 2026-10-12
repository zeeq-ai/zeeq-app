using Zeeq.Core.Models;

namespace Zeeq.Testing.Biomes;

/// <summary>Mutable construction input for CodeRepository test output.</summary>
public sealed class CodeRepositoryInput
{
    /// <summary>
    /// Provider key such as <c>github</c>.
    /// </summary>
    public string Provider { get; set; } = "github";

    /// <summary>
    /// Provider-qualified repository name. A generated <c>owner/repo</c> name is used when omitted.
    /// </summary>
    public string? OwnerQualifiedName { get; set; }

    /// <summary>
    /// Human-readable display name. Defaults to <see cref="OwnerQualifiedName" />.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether the generated repository mapping is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the repository appears as a selectable private library source.
    /// </summary>
    public bool VisibleInLibraryPicker { get; set; } = true;

    /// <summary>
    /// Optional Zeeq team owner. Defaults to no team-level owner.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// Typed repository review configuration.
    /// </summary>
    public CodeRepositoryReviewConfiguration ReviewConfiguration { get; set; } =
        CodeRepositoryReviewConfiguration.Empty;

    /// <summary>
    /// Library ids scoped to this repository for reviewer-agent tool access.
    /// </summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>
    /// Created timestamp for the generated repository row.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }
}

/// <summary>Mutable construction input for PullRequestLookup test output.</summary>
public sealed class PullRequestLookupInput
{
    /// <summary>
    /// Provider pull request number.
    /// </summary>
    public int? PullRequestNumber { get; set; }

    /// <summary>
    /// Partitioned pull request record ID currently targeted by the lookup.
    /// </summary>
    public string? PullRequestRecordId { get; set; }

    /// <summary>
    /// Created timestamp for the targeted partitioned pull request record.
    /// </summary>
    public DateTimeOffset? PullRequestCreatedAtUtc { get; set; }

    /// <summary>
    /// Lookup updated timestamp.
    /// </summary>
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

/// <summary>Mutable construction input for PullRequestRecord test output.</summary>
public sealed class PullRequestRecordInput
{
    /// <summary>
    /// Provider pull request number.
    /// </summary>
    public int? PullRequestNumber { get; set; }

    /// <summary>
    /// Pull request created timestamp. This is also the partition key.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Pull request lifecycle state.
    /// </summary>
    public PullRequestState State { get; set; } = PullRequestState.Open;

    /// <summary>
    /// Zeeq review claim state for the pull request.
    /// </summary>
    public PullRequestClaimStatus ClaimStatus { get; set; } = PullRequestClaimStatus.Unclaimed;

    /// <summary>
    /// Pull request title. A generated title is used when omitted.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Pull request source branch. A generated branch is used when omitted.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Pull request target branch.
    /// </summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>
    /// Provider login for the pull request author.
    /// </summary>
    public string AuthorLogin { get; set; } = "octocat";
}

/// <summary>Mutable construction input for CodeReviewRecord test output.</summary>
public sealed class CodeReviewRecordInput
{
    /// <summary>
    /// Review created timestamp. This is also the partition key.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Current review execution status.
    /// </summary>
    public CodeReviewStatus Status { get; set; } = CodeReviewStatus.Pending;

    /// <summary>
    /// Expiry for the generated active lock. Defaults to a live two-hour TTL.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Source that requested the review.
    /// </summary>
    public CodeReviewRequestOrigin RequestOrigin { get; set; } =
        CodeReviewRequestOrigin.RepositoryWebhook;

    /// <summary>
    /// Remaining budget for review attempts or retries.
    /// </summary>
    public int RemainingReviewBudget { get; set; } = 10;
}

/// <summary>Construction input for an active review guard row.</summary>
public sealed class ActiveCodeReviewLockInput
{
    /// <summary>Active review status stored on the lock.</summary>
    public CodeReviewStatus Status { get; set; } = CodeReviewStatus.Pending;

    /// <summary>Expiry; defaults to four minutes after the session time.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

/// <summary>Construction input for a GitHub webhook delivery.</summary>
public sealed class GitHubWebhookDeliveryInput
{
    /// <summary>Delivery identifier; generated when omitted.</summary>
    public string? DeliveryId { get; set; }
}
