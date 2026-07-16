using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for the non-partitioned pull request lookup row.
/// </summary>
/// <remarks>
/// Pull request records are partitioned by <c>CreatedAtUtc</c>, so Postgres
/// cannot enforce uniqueness on provider identity without including the
/// partition key. This compact lookup table owns the cross-partition invariant
/// for organization, repository, and pull request number, and points to the
/// current partitioned record via ID plus creation timestamp.
/// </remarks>
internal sealed class PostgresPullRequestLookupStore(PostgresDbContext db) : IPullRequestLookupStore
{
    /// <summary>
    /// Finds the lookup row for a provider pull request identity.
    /// </summary>
    /// <remarks>
    /// Handlers use this row to jump from a GitHub webhook payload to the
    /// partitioned pull request record without scanning historical partitions.
    /// </remarks>
    public Task<PullRequestLookup?> FindAsync(
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    ) =>
        db
            .PullRequestLookups.TagWithOperationCallSite("pull_request_lookup.find")
            .FirstOrDefaultAsync(
                lookup =>
                    lookup.OrganizationId == organizationId
                    && lookup.RepositoryId == repositoryId
                    && lookup.PullRequestNumber == pullRequestNumber,
                cancellationToken
            );

    /// <summary>
    /// Creates or updates the lookup pointer for a provider pull request.
    /// </summary>
    /// <remarks>
    /// The transaction-scoped advisory lock serializes duplicate webhook
    /// deliveries for the same PR before they touch the lookup row. The primary
    /// key remains the durable invariant, and transaction locks are used because
    /// PgBouncer transaction pooling can break session-lock ownership.
    /// </remarks>
    public async Task<PullRequestLookup> UpsertAsync(
        PullRequestLookup lookup,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
        var activeTransaction =
            db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Pull request lookup upsert requires an active database transaction."
            );

        // Acquire after the transaction exists so the advisory lock is scoped
        // to commit/rollback rather than to a pooled physical session.
        await PostgresCodeReviewStoreAdvisoryLocks.AcquirePullRequestLookupAsync(
            activeTransaction,
            lookup.OrganizationId,
            lookup.RepositoryId,
            lookup.PullRequestNumber,
            cancellationToken
        );

        var existing = await FindAsync(
            lookup.OrganizationId,
            lookup.RepositoryId,
            lookup.PullRequestNumber,
            cancellationToken
        );

        if (existing is null)
        {
            db.PullRequestLookups.Add(lookup);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return lookup;
        }

        // Keep the compact lookup row pointed at the newest durable PR record.
        // The historical record remains in its time partition.
        existing.TeamId = lookup.TeamId;
        existing.OwnerQualifiedRepoName = lookup.OwnerQualifiedRepoName;
        existing.PullRequestRecordId = lookup.PullRequestRecordId;
        existing.PullRequestCreatedAtUtc = lookup.PullRequestCreatedAtUtc;
        existing.UpdatedAtUtc = lookup.UpdatedAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return existing;
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
