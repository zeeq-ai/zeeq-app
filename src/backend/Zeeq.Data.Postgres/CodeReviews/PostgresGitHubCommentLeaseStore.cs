using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed lease store for serialized GitHub comment writes.
/// </summary>
/// <remarks>
/// Acquiring a lease is a tiny database operation protected by a
/// transaction-scoped advisory lock. The caller releases the transaction before
/// it talks to GitHub, then periodically renews the unlogged lease row while the
/// network write is running. This avoids holding a database transaction across
/// GitHub API calls while still keeping one active writer per comment target.
/// </remarks>
internal sealed class PostgresGitHubCommentLeaseStore(
    PostgresDbContext db,
    IDbContextFactory<PostgresDbContext> dbFactory
) : IGitHubCommentLeaseStore
{
    /// <summary>
    /// Attempts to create or take over the lease row for one comment target.
    /// </summary>
    /// <remarks>
    /// The advisory lock only protects the local acquire/update decision. It is
    /// transaction-scoped because PgBouncer can detach session locks from the
    /// caller under transaction pooling. Time is based on the app clock; small
    /// skew across workers is acceptable because the lease only gates best-effort
    /// comment rendering and can be repaired by later queue signals.
    /// </remarks>
    public async Task<bool> TryAcquireAsync(
        GitHubCommentLeaseKey key,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
        var activeTransaction =
            db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "GitHub comment lease acquisition requires an active database transaction."
            );

        await PostgresCodeReviewStoreAdvisoryLocks.AcquireGitHubCommentLeaseAsync(
            activeTransaction,
            key,
            cancellationToken
        );

        var leaseKey = key.ToString();
        var now = DateTimeOffset.UtcNow;
        var existing = await db
            .GitHubCommentLeases.TagWithOperationCallSite("github_comment_lease.try_acquire_find")
            .AsNoTracking()
            .SingleOrDefaultAsync(lease => lease.LeaseKey == leaseKey, cancellationToken);

        if (existing is not null && existing.ExpiresAtUtc > now)
        {
            return false;
        }

        if (existing is null)
        {
            db.GitHubCommentLeases.Add(
                new GitHubCommentLease
                {
                    LeaseKey = leaseKey,
                    WorkerId = workerId,
                    AcquiredAtUtc = now,
                    ExpiresAtUtc = now.Add(leaseDuration),
                }
            );
        }
        else
        {
            // Expired rows are taken over in place so the primary key stays
            // stable while ownership moves to the current worker.
            await db
                .GitHubCommentLeases.TagWithOperationCallSite(
                    "github_comment_lease.try_acquire_takeover"
                )
                .Where(lease => lease.LeaseKey == leaseKey)
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(lease => lease.WorkerId, workerId)
                            .SetProperty(lease => lease.AcquiredAtUtc, now)
                            .SetProperty(lease => lease.ExpiresAtUtc, now.Add(leaseDuration)),
                    cancellationToken
                );
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Renews a lease only when the same worker still owns it.
    /// </summary>
    public async Task<bool> RenewAsync(
        GitHubCommentLeaseKey key,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        // Renewal runs on a task that is concurrent with the handler's
        // render/write task. That work task uses scoped stores backed by the
        // request scope's PostgresDbContext. EF DbContext is not thread-safe, so
        // renewal must use a fresh context from the factory.
        await using var renewDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var rowsAffected = await renewDb
            .GitHubCommentLeases.TagWithOperationCallSite("github_comment_lease.renew")
            .Where(lease => lease.LeaseKey == key.ToString() && lease.WorkerId == workerId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(lease => lease.ExpiresAtUtc, now.Add(leaseDuration)),
                cancellationToken
            );

        return rowsAffected == 1;
    }

    /// <summary>
    /// Deletes the lease row only when the current worker owns it.
    /// </summary>
    public async Task ReleaseAsync(
        GitHubCommentLeaseKey key,
        string workerId,
        CancellationToken cancellationToken
    ) =>
        await db
            .GitHubCommentLeases.TagWithOperationCallSite("github_comment_lease.release")
            .Where(lease => lease.LeaseKey == key.ToString() && lease.WorkerId == workerId)
            .ExecuteDeleteAsync(cancellationToken);

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
