using System.Runtime.CompilerServices;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

internal static class PostgresCodeReviewStoreAdvisoryLocks
{
    private static readonly Lock AcquiredLocksLock = new();
    private static readonly ConditionalWeakTable<
        IDbContextTransaction,
        AcquiredTransactionLocks
    > AcquiredLocks = [];

    public static ValueTask AcquirePullRequestLookupAsync(
        IDbContextTransaction transaction,
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    ) =>
        AcquireTransactionLockAsync(
            transaction,
            $"zeeq:code-reviews:pull-request-lookup:{organizationId}:{repositoryId}:{pullRequestNumber}",
            cancellationToken
        );

    public static ValueTask AcquireActiveReviewAsync(
        IDbContextTransaction transaction,
        string organizationId,
        string pullRequestRecordId,
        CancellationToken cancellationToken
    ) =>
        AcquireTransactionLockAsync(
            transaction,
            $"zeeq:code-reviews:active-review:{organizationId}:{pullRequestRecordId}",
            cancellationToken
        );

    public static ValueTask AcquireExecutionCapacityAsync(
        IDbContextTransaction transaction,
        string organizationId,
        CancellationToken cancellationToken
    ) =>
        AcquireTransactionLockAsync(
            transaction,
            $"zeeq:code-reviews:execution-capacity:{organizationId}",
            cancellationToken
        );

    public static ValueTask AcquireGitHubCommentLeaseAsync(
        IDbContextTransaction transaction,
        GitHubCommentLeaseKey key,
        CancellationToken cancellationToken
    ) =>
        AcquireTransactionLockAsync(
            transaction,
            $"zeeq:code-reviews:github-comment-lease:{key}",
            cancellationToken
        );

    /// <summary>
    /// Serializes evaluation admission for one organization.
    /// </summary>
    /// <remarks>
    /// Acquired before any dataset or run row lock so the "one active evaluation per
    /// organization" rule cannot race two admits past the partial unique index.
    /// </remarks>
    public static ValueTask AcquireEvaluationAdmissionAsync(
        IDbContextTransaction transaction,
        string organizationId,
        CancellationToken cancellationToken
    ) =>
        AcquireTransactionLockAsync(
            transaction,
            $"zeeq:code-reviews:evaluation-admission:{organizationId}",
            cancellationToken
        );

    /// <summary>
    /// Serializes dataset saves and admission manifest copies for one dataset row.
    /// </summary>
    public static ValueTask AcquireEvaluationDatasetAsync(
        IDbContextTransaction transaction,
        string organizationId,
        string datasetId,
        CancellationToken cancellationToken
    ) =>
        AcquireTransactionLockAsync(
            transaction,
            $"zeeq:code-reviews:evaluation-dataset:{organizationId}:{datasetId}",
            cancellationToken
        );

    private static async ValueTask AcquireTransactionLockAsync(
        IDbContextTransaction transaction,
        string name,
        CancellationToken cancellationToken
    )
    {
        if (HasAcquired(transaction, name))
        {
            return;
        }

        await PostgresDistributedLock.AcquireWithTransactionAsync(
            new PostgresAdvisoryLockKey(name, allowHashing: true),
            transaction.GetDbTransaction(),
            timeout: null,
            cancellationToken
        );

        MarkAcquired(transaction, name);
    }

    private static bool HasAcquired(IDbContextTransaction transaction, string name)
    {
        lock (AcquiredLocksLock)
        {
            return AcquiredLocks.TryGetValue(transaction, out var acquired)
                && acquired.Names.Contains(name);
        }
    }

    private static void MarkAcquired(IDbContextTransaction transaction, string name)
    {
        lock (AcquiredLocksLock)
        {
            AcquiredLocks.GetOrCreateValue(transaction).Names.Add(name);
        }
    }

    private sealed class AcquiredTransactionLocks
    {
        public HashSet<string> Names { get; } = [];
    }
}
