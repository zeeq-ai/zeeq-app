using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for partitioned pull request records.
/// </summary>
/// <remarks>
/// Pull request records are the recent-stream DTO source for the code-review
/// UI. They are partitioned by <c>CreatedAtUtc</c> for fast inserts and recent
/// reads, while <see cref="IPullRequestLookupStore" /> owns provider identity
/// uniqueness across partitions.
/// </remarks>
public interface IPullRequestRecordStore
{
    /// <summary>
    /// Creates or updates a pull request record.
    /// </summary>
    /// <remarks>
    /// Webhook handlers use this to refresh mutable GitHub state such as title,
    /// head SHA, draft state, labels, and branch names.
    /// </remarks>
    Task<PullRequestRecord> UpsertAsync(
        PullRequestRecord pullRequest,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds one pull request record by stable id and partition timestamp.
    /// </summary>
    Task<PullRequestRecord?> FindAsync(
        string id,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds the most recent pull request whose recorded head SHA matches the
    /// given commit, across all partitions.
    /// </summary>
    /// <remarks>
    /// This is a reverse-lookup fallback for check-run bypass and other webhook
    /// paths where GitHub loses the PR association (for example after a stacked-
    /// branch collapse). The scan is bounded to one organization and repository,
    /// and bypass events are human-initiated so the per-request cost is acceptable.
    /// </remarks>
    Task<PullRequestRecord?> FindByHeadShaAsync(
        string organizationId,
        string repositoryId,
        string headSha,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds a record whose head SHA matches, whose check-run state is non-null,
    /// and which is open, across all partitions. When multiple PRs share the same
    /// head SHA (for example after a stacked-branch collapse), this resolves to the
    /// one that actually has an active check run to bypass.
    /// </summary>
    Task<PullRequestRecord?> FindByHeadShaWithCheckRunAsync(
        string organizationId,
        string repositoryId,
        string headSha,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns every record for a provider pull request number, newest first,
    /// across all partitions. Use when the newest record lacks the needed state
    /// and older rows must be searched (e.g. a check-run state that was stored
    /// against a prior head SHA after a stacked-branch collapse).
    /// </summary>
    Task<IReadOnlyList<PullRequestRecord>> FindByNumberAsync(
        string organizationId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists recent pull request records for stream views.
    /// </summary>
    /// <remarks>
    /// Results are newest first and use seek pagination with
    /// <c>CreatedAtUtc</c> plus id so refreshes and infinite scroll can reuse a
    /// known cursor.
    /// </remarks>
    Task<CodeReviewStreamPage<PullRequestRecord>> ListRecentAsync(
        PullRequestStreamQuery query,
        CancellationToken cancellationToken
    );
}
