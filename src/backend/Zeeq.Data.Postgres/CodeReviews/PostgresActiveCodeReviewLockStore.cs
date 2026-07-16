using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for the one-active-review-per-pull-request guard row.
/// </summary>
/// <remarks>
/// The active lock table is the durable invariant that prevents duplicate
/// review runs for the same pull request. The transaction-scoped advisory lock
/// used here only serializes competing writers before they touch the guard row;
/// the unique key remains the final correctness boundary.
/// </remarks>
internal sealed class PostgresActiveCodeReviewLockStore(PostgresDbContext db)
    : IActiveCodeReviewLockStore
{
    /// <summary>
    /// Attempts to create the active review guard row.
    /// </summary>
    /// <remarks>
    /// This method returns <see langword="false" /> when a review is already
    /// active. It starts a transaction only when the caller has not already
    /// supplied one, so webhook processing can compose this with larger unit of
    /// work boundaries later.
    /// </remarks>
    public async Task<bool> TryAcquireAsync(
        ActiveCodeReviewLock activeLock,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
        var activeTransaction =
            db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Active code review lock acquisition requires an active database transaction."
            );

        // Use an xact-scoped advisory lock, not a session lock. PgBouncer can
        // break session-lock ownership when transaction pooling is enabled.
        await PostgresCodeReviewStoreAdvisoryLocks.AcquireActiveReviewAsync(
            activeTransaction,
            activeLock.OrganizationId,
            activeLock.PullRequestRecordId,
            cancellationToken
        );

        await ReapExpiredForPullRequestAsync(activeLock, cancellationToken);

        var existing = await FindAsync(
            activeLock.OrganizationId,
            activeLock.PullRequestRecordId,
            cancellationToken
        );

        // Check after the advisory lock so normal duplicate webhook deliveries
        // avoid a unique-violation path and keep ambient transactions usable.
        if (existing is not null)
        {
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return false;
        }

        db.ActiveCodeReviewLocks.Add(activeLock);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return true;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            db.Entry(activeLock).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Finds the current active review guard row for a pull request.
    /// </summary>
    public Task<ActiveCodeReviewLock?> FindAsync(
        string organizationId,
        string pullRequestRecordId,
        CancellationToken cancellationToken
    ) =>
        db
            .ActiveCodeReviewLocks.TagWithOperationCallSite("active_code_review_lock.find")
            .FirstOrDefaultAsync(
                activeLock =>
                    activeLock.OrganizationId == organizationId
                    && activeLock.PullRequestRecordId == pullRequestRecordId,
                cancellationToken
            );

    /// <summary>
    /// Pushes the active review guard expiry forward.
    /// </summary>
    public async Task<bool> RefreshAsync(
        string organizationId,
        string pullRequestRecordId,
        TimeSpan ttl,
        CancellationToken cancellationToken
    )
    {
        var activeLock = await FindAsync(organizationId, pullRequestRecordId, cancellationToken);
        if (activeLock is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        activeLock.ExpiresAtUtc = now.Add(ttl);
        activeLock.UpdatedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Removes the active review guard row after the review reaches a terminal state.
    /// </summary>
    /// <remarks>
    /// Missing rows are treated as already released. That keeps completion and
    /// cleanup handlers idempotent when queue retries replay after a successful
    /// database write.
    /// </remarks>
    public async Task ReleaseAsync(
        string organizationId,
        string pullRequestRecordId,
        CancellationToken cancellationToken
    )
    {
        var activeLock = await FindAsync(organizationId, pullRequestRecordId, cancellationToken);
        if (activeLock is null)
        {
            return;
        }

        db.ActiveCodeReviewLocks.Remove(activeLock);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the active review guard only when it still belongs to the expected review row.
    /// </summary>
    public async Task ReleaseIfOwnedByReviewAsync(
        string organizationId,
        string pullRequestRecordId,
        string codeReviewRecordId,
        DateTimeOffset codeReviewCreatedAtUtc,
        CancellationToken cancellationToken
    )
    {
        var activeLock = await db
            .ActiveCodeReviewLocks.TagWithOperationCallSite(
                "active_code_review_lock.release_if_owned_by_review"
            )
            .FirstOrDefaultAsync(
                row =>
                    row.OrganizationId == organizationId
                    && row.PullRequestRecordId == pullRequestRecordId
                    && row.CodeReviewRecordId == codeReviewRecordId
                    && row.CodeReviewCreatedAtUtc == codeReviewCreatedAtUtc,
                cancellationToken
            );

        if (activeLock is null)
        {
            return;
        }

        db.ActiveCodeReviewLocks.Remove(activeLock);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Detects the durable unique-key fallback when two writers still race.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Reaps an expired lock for the same PR and marks its review terminal.
    /// </summary>
    private async Task ReapExpiredForPullRequestAsync(
        ActiveCodeReviewLock candidate,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db
            .ActiveCodeReviewLocks.TagWithOperationCallSite(
                "active_code_review_lock.reap_expired_for_pull_request"
            )
            .FirstOrDefaultAsync(
                activeLock =>
                    activeLock.OrganizationId == candidate.OrganizationId
                    && activeLock.PullRequestRecordId == candidate.PullRequestRecordId
                    && activeLock.ExpiresAtUtc <= now,
                cancellationToken
            );

        if (expired is null)
        {
            return;
        }

        var review = await db
            .CodeReviewRecords.TagWithOperationCallSite(
                "active_code_review_lock.reconcile_expired_review"
            )
            .FirstOrDefaultAsync(
                record =>
                    record.Id == expired.CodeReviewRecordId
                    && record.CreatedAtUtc == expired.CodeReviewCreatedAtUtc,
                cancellationToken
            );

        if (review is { Status: CodeReviewStatus.Pending or CodeReviewStatus.Running })
        {
            review.Status = CodeReviewStatus.Errored;
            review.FailureMessage =
                "Code review active lock expired before the review reached a terminal state.";
            review.UpdatedAtUtc = now;
        }

        db.ActiveCodeReviewLocks.Remove(expired);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Starts a transaction only when the caller has not already provided one.
    /// </summary>
    private async ValueTask<IDbContextTransaction?> BeginTransactionIfNeededAsync(
        CancellationToken cancellationToken
    ) =>
        db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
}
