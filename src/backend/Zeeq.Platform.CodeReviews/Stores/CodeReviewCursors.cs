using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Cursor boundary for newest-first code-review streams.
/// </summary>
/// <param name="CreatedAtUtc">Full timestamp used by the partitioned table.</param>
/// <param name="Id">Stable row id used as the tie-breaker.</param>
public sealed record CodeReviewStreamCursor(DateTimeOffset CreatedAtUtc, string Id);

/// <summary>
/// Page of stream results with the next cursor boundary.
/// </summary>
/// <typeparam name="T">Row type returned by the store.</typeparam>
public sealed record CodeReviewStreamPage<T>(
    IReadOnlyList<T> Items,
    CodeReviewStreamCursor? NextCursor,
    CodeReviewStreamCursor? NewestCursor
);

/// <summary>
/// Logical inbox stream scope for PR review updates.
/// </summary>
public enum CodeReviewInboxScope
{
    /// <summary>
    /// Include all PRs visible to the organization/team/repository filter.
    /// </summary>
    All,

    /// <summary>
    /// Include PRs scoped to the authenticated user.
    /// </summary>
    Mine,
}

/// <summary>
/// Cursor boundary for ascending inbox review update streams.
/// </summary>
/// <remarks>
/// The update feed is ordered by mutable <c>UpdatedAtUtc</c>, but the review
/// table is partitioned by <c>CreatedAtUtc</c>. The cursor therefore carries a
/// fixed review-created lower bound for partition pruning plus the complete
/// update ordering tuple used to continue polling without duplicates.
/// </remarks>
public sealed record CodeReviewUpdateCursor(
    DateTimeOffset ReviewCreatedAtLowerBoundUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset CreatedAtUtc,
    string Id,
    string? TeamId = null,
    string? RepositoryId = null,
    CodeReviewInboxScope Scope = CodeReviewInboxScope.All,
    string? SubjectUserId = null
)
{
    /// <summary>
    /// Non-row sentinel id used by synthetic high-water cursors.
    /// </summary>
    /// <remarks>
    /// The endpoint parser treats blank ids as incomplete cursors. Initial poll
    /// cursors still need an id so clients preserve the <c>UpdatedAtUtc</c>
    /// boundary instead of replaying the whole inbox window on first poll.
    /// </remarks>
    public const string SyntheticHighWaterId = "__synthetic_high_water__";
}

/// <summary>
/// Filter for recent pull request stream queries.
/// </summary>
public sealed record PullRequestStreamQuery(
    string OrganizationId,
    string? TeamId = null,
    string? RepositoryId = null,
    PullRequestClaimStatus? ClaimStatus = null,
    string? SubjectUserId = null,
    CodeReviewStreamCursor? Cursor = null,
    int PageSize = 50
);

/// <summary>
/// Filter for recent code review stream queries.
/// </summary>
public sealed record CodeReviewStreamQuery(
    string OrganizationId,
    string? TeamId = null,
    string? RepositoryId = null,
    CodeReviewStatus? Status = null,
    CodeReviewStreamCursor? Cursor = null,
    int PageSize = 50
);

/// <summary>
/// Filter for one pull request's review history.
/// </summary>
/// <remarks>
/// The selected pull request's creation timestamp is part of the query because
/// reviews cannot predate the pull request. Implementations use it as the
/// partition lower bound for the partitioned review table.
/// </remarks>
public sealed record PullRequestReviewStreamQuery(
    string OrganizationId,
    string PullRequestRecordId,
    DateTimeOffset PullRequestCreatedAtUtc,
    CodeReviewStreamCursor? Cursor = null,
    int PageSize = 50
);

/// <summary>
/// Filter for minimal review updates consumed by the PR inbox.
/// </summary>
public sealed record CodeReviewUpdateStreamQuery(
    string OrganizationId,
    string? TeamId = null,
    string? RepositoryId = null,
    CodeReviewInboxScope Scope = CodeReviewInboxScope.All,
    string? SubjectUserId = null,
    DateTimeOffset? ReviewCreatedAtLowerBoundUtc = null,
    CodeReviewUpdateCursor? Cursor = null,
    int PageSize = 50
);

/// <summary>
/// Minimal review update for patching the pull request inbox.
/// </summary>
/// <remarks>
/// The row intentionally excludes findings payload data. The inbox needs status
/// and count summaries only; full review details are loaded through the selected
/// pull request's review-history endpoint.
/// </remarks>
public sealed record CodeReviewInboxUpdate(
    string PullRequestRecordId,
    DateTimeOffset PullRequestCreatedAtUtc,
    string CodeReviewRecordId,
    DateTimeOffset CodeReviewCreatedAtUtc,
    CodeReviewStatus Status,
    int CriticalFindings,
    int MajorFindings,
    int MinorFindings,
    int SuggestionFindings,
    int CommentFindings,
    int RemainingReviewBudget,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>
/// Page of minimal inbox review updates.
/// </summary>
public sealed record CodeReviewUpdateStreamPage(
    IReadOnlyList<CodeReviewInboxUpdate> Items,
    CodeReviewUpdateCursor? NextCursor,
    CodeReviewUpdateCursor? NewestCursor
);
