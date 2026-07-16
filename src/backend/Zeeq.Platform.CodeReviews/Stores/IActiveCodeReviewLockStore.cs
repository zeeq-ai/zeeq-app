using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for the non-partitioned active-review guard table.
/// </summary>
/// <remarks>
/// This contract owns the "one active review per pull request" rule for the
/// GitHub review workflow. Implementations may use cooperative locks to reduce
/// contention, but the durable guard row is the source of truth. The interface
/// is provider-neutral and intentionally portable to a future shared code-review
/// storage package.
/// </remarks>
public interface IActiveCodeReviewLockStore
{
    /// <summary>
    /// Attempts to create the active-review guard row.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false" /> when another review is already active
    /// for the pull request. Callers should treat that as an expected gate, not
    /// as an infrastructure failure.
    /// </remarks>
    Task<bool> TryAcquireAsync(
        ActiveCodeReviewLock activeLock,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds the active-review guard row for a pull request.
    /// </summary>
    Task<ActiveCodeReviewLock?> FindAsync(
        string organizationId,
        string pullRequestRecordId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Pushes the active-review guard expiry forward while work is still live.
    /// </summary>
    Task<bool> RefreshAsync(
        string organizationId,
        string pullRequestRecordId,
        TimeSpan ttl,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Releases the active-review guard after review work reaches a terminal state.
    /// </summary>
    /// <remarks>
    /// Release should be idempotent. Queue retries can replay completion work
    /// after the guard has already been removed.
    /// </remarks>
    Task ReleaseAsync(
        string organizationId,
        string pullRequestRecordId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Releases the active-review guard only when it still points at the expected review.
    /// </summary>
    /// <remarks>
    /// Queue redeliveries can replay terminal cleanup after a newer review has
    /// acquired the same pull-request guard. Callers that are completing a
    /// specific review should use this owner-checked release so stale messages
    /// cannot remove another review's lock.
    /// </remarks>
    Task ReleaseIfOwnedByReviewAsync(
        string organizationId,
        string pullRequestRecordId,
        string codeReviewRecordId,
        DateTimeOffset codeReviewCreatedAtUtc,
        CancellationToken cancellationToken
    );
}
