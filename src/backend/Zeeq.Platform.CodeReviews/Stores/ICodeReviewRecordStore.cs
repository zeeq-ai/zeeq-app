using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for partitioned code review records.
/// </summary>
/// <remarks>
/// Code review records are the durable execution history for review attempts.
/// The store uses full <c>CreatedAtUtc</c> values in detail lookups and cursors
/// because Postgres partitions these rows by creation timestamp. This contract
/// is intentionally provider-neutral and can move to a shared code-review
/// storage home later.
/// </remarks>
public interface ICodeReviewRecordStore
{
    /// <summary>
    /// Adds a new code review execution record.
    /// </summary>
    /// <remarks>
    /// A new review attempt should create a new row. The active-review guard,
    /// not this table, decides whether a new attempt is allowed.
    /// </remarks>
    Task<CodeReviewRecord> AddAsync(CodeReviewRecord review, CancellationToken cancellationToken);

    /// <summary>
    /// Updates an existing code review execution record.
    /// </summary>
    /// <remarks>
    /// Runner handlers use this to move the partitioned review row through
    /// running and terminal states. Callers must provide the original
    /// <c>CreatedAtUtc</c> value on the record so implementations can address
    /// the correct partition.
    /// </remarks>
    Task<CodeReviewRecord> UpdateAsync(
        CodeReviewRecord review,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds one review record by stable id and partition timestamp.
    /// </summary>
    Task<CodeReviewRecord?> FindAsync(
        string id,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds the newest review record for one pull request.
    /// </summary>
    /// <remarks>
    /// Workflow policy uses this to enforce the remaining review budget before
    /// accepting another review request. The method is intentionally scoped to
    /// the already-resolved partitioned PR record id so providers can use the
    /// workflow index instead of scanning the recent-review stream.
    /// </remarks>
    Task<CodeReviewRecord?> FindNewestForPullRequestAsync(
        string organizationId,
        string pullRequestRecordId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds the newest completed review in one exact agent review group.
    /// </summary>
    /// <remarks>
    /// Used only to establish an OpenTelemetry causal link for a follow-up review. Concurrent
    /// follow-ups may select the same predecessor; ties are broken by review id for stability.
    /// </remarks>
    Task<CodeReviewRecord?> FindNewestCompletedForReviewGroupAsync(
        string organizationId,
        string reviewGroupId,
        CancellationToken cancellationToken
    ) => Task.FromResult<CodeReviewRecord?>(null);

    /// <summary>
    /// Lists recent code review records for stream views.
    /// </summary>
    /// <remarks>
    /// Results are newest first and use seek pagination with
    /// <c>CreatedAtUtc</c> plus id.
    /// </remarks>
    Task<CodeReviewStreamPage<CodeReviewRecord>> ListRecentAsync(
        CodeReviewStreamQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists review records for one selected pull request.
    /// </summary>
    /// <remarks>
    /// The query includes the pull request creation timestamp so implementations
    /// can apply a review <c>CreatedAtUtc</c> lower bound before paging. That
    /// preserves partition pruning while still returning review history by the
    /// stable pull request record ID.
    /// </remarks>
    Task<CodeReviewStreamPage<CodeReviewRecord>> ListForPullRequestAsync(
        PullRequestReviewStreamQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists minimal review updates for PR inbox refresh.
    /// </summary>
    /// <remarks>
    /// This stream is ordered by <c>UpdatedAtUtc</c> so a loaded inbox can poll
    /// for state changes without opening every PR. The cursor still carries a
    /// review-created lower bound because <see cref="CodeReviewRecord"/> is
    /// partitioned by <c>CreatedAtUtc</c>.
    /// </remarks>
    Task<CodeReviewUpdateStreamPage> ListInboxUpdatesAsync(
        CodeReviewUpdateStreamQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists agent reviews for the single-review related set, newest first, matching by
    /// session id and/or group id (either). Returns empty when both keys are null.
    /// </summary>
    Task<IReadOnlyList<CodeReviewRecord>> ListForAgentAsync(
        string organizationId,
        string? agentSessionId,
        string? reviewGroupId,
        int maxRecords,
        CancellationToken cancellationToken
    );
}
