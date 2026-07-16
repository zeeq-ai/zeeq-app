using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed execution-capacity lease store.
/// </summary>
internal sealed partial class PostgresCodeReviewExecutionLeaseStore(
    PostgresDbContext db,
    ILogger<PostgresCodeReviewExecutionLeaseStore> logger
) : ICodeReviewExecutionLeaseStore
{
    /// <inheritdoc />
    public async Task<CodeReviewExecutionLeaseResult> TryAcquireAsync(
        CodeReviewExecutionLeaseRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);

        LogLeaseTransactionReady(
            logger,
            request.OrganizationId,
            request.CodeReviewRecordId,
            transaction is not null
        );

        var activeTransaction =
            db.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Code review execution lease acquisition requires an active database transaction."
            );

        await PostgresCodeReviewStoreAdvisoryLocks.AcquireExecutionCapacityAsync(
            activeTransaction,
            request.OrganizationId,
            cancellationToken
        );

        LogLeaseAdvisoryLockAcquired(logger, request.OrganizationId, request.CodeReviewRecordId);

        var now = DateTimeOffset.UtcNow;

        var deletedExpiredCount = await DeleteExpiredAsync(
            request.OrganizationId,
            now,
            cancellationToken
        );

        var alreadyLeased = await db
            .CodeReviewExecutionLeases.TagWithOperationCallSite(
                "code_review_execution_lease.find_existing_for_review"
            )
            .FirstOrDefaultAsync(
                lease =>
                    lease.OrganizationId == request.OrganizationId
                    && lease.CodeReviewRecordId == request.CodeReviewRecordId
                    && lease.ExpiresAtUtc > now,
                cancellationToken
            );

        if (alreadyLeased is not null)
        {
            await CommitIfOwnedAsync(transaction, cancellationToken);

            LogLeaseAcquisitionCompleted(
                logger,
                request.OrganizationId,
                request.CodeReviewRecordId,
                CodeReviewExecutionLeaseOutcome.AlreadyLeasedForThisReview,
                alreadyLeased.LeaseId,
                alreadyLeased.SlotIndex
            );

            return new(CodeReviewExecutionLeaseOutcome.AlreadyLeasedForThisReview, alreadyLeased);
        }

        var maxConcurrentReviews = Math.Max(2, request.MaxConcurrentReviews);

        var liveSlots = await db
            .CodeReviewExecutionLeases.TagWithOperationCallSite(
                "code_review_execution_lease.list_live_slots"
            )
            .Where(lease =>
                lease.OrganizationId == request.OrganizationId && lease.ExpiresAtUtc > now
            )
            .Select(lease => lease.SlotIndex)
            .ToArrayAsync(cancellationToken);

        LogLiveExecutionLeasesLoaded(
            logger,
            request.OrganizationId,
            request.CodeReviewRecordId,
            liveSlots.Length,
            maxConcurrentReviews
        );

        var live = liveSlots.ToHashSet();

        var slotIndex = Enumerable
            .Range(0, maxConcurrentReviews)
            .FirstOrDefault(slot => !live.Contains(slot), -1);

        if (slotIndex < 0)
        {
            await CommitIfOwnedAsync(transaction, cancellationToken);

            LogLeaseAcquisitionCompleted(
                logger,
                request.OrganizationId,
                request.CodeReviewRecordId,
                CodeReviewExecutionLeaseOutcome.NoSlotAvailable,
                null,
                null
            );

            return new(CodeReviewExecutionLeaseOutcome.NoSlotAvailable, null);
        }

        var lease = new CodeReviewExecutionLease
        {
            Id = "crl_" + Guid.CreateVersion7().ToString("N"),
            OrganizationId = request.OrganizationId,
            TeamId = request.TeamId,
            SlotIndex = slotIndex,
            LeaseId = "crlease_" + Guid.CreateVersion7().ToString("N"),
            RepositoryId = request.RepositoryId,
            PullRequestRecordId = request.PullRequestRecordId,
            PullRequestCreatedAtUtc = request.PullRequestCreatedAtUtc,
            CodeReviewRecordId = request.CodeReviewRecordId,
            CodeReviewCreatedAtUtc = request.CodeReviewCreatedAtUtc,
            AcquiredAtUtc = now,
            RenewedAtUtc = now,
            ExpiresAtUtc = now.Add(request.LeaseDuration),
            WorkerId = request.WorkerId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.CodeReviewExecutionLeases.Add(lease);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await CommitIfOwnedAsync(transaction, cancellationToken);

            return new(CodeReviewExecutionLeaseOutcome.Acquired, lease);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            LogLeaseUniqueViolation(
                logger,
                exception,
                request.OrganizationId,
                request.CodeReviewRecordId,
                lease.LeaseId,
                lease.SlotIndex
            );

            db.Entry(lease).State = EntityState.Detached;

            await CommitIfOwnedAsync(transaction, cancellationToken);

            var existing = await db
                .CodeReviewExecutionLeases.TagWithOperationCallSite(
                    "code_review_execution_lease.find_after_unique_violation"
                )
                .FirstOrDefaultAsync(
                    row =>
                        row.OrganizationId == request.OrganizationId
                        && row.CodeReviewRecordId == request.CodeReviewRecordId
                        && row.ExpiresAtUtc > now,
                    cancellationToken
                );

            return existing is not null
                ? new(CodeReviewExecutionLeaseOutcome.AlreadyLeasedForThisReview, existing)
                : new(CodeReviewExecutionLeaseOutcome.NoSlotAvailable, null);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RenewAsync(
        string leaseId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        var lease = await db
            .CodeReviewExecutionLeases.TagWithOperationCallSite(
                "code_review_execution_lease.renew_find"
            )
            .FirstOrDefaultAsync(row => row.LeaseId == leaseId, cancellationToken);

        if (lease is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lease.RenewedAtUtc = now;
        lease.ExpiresAtUtc = now.Add(leaseDuration);
        lease.UpdatedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string leaseId, CancellationToken cancellationToken)
    {
        var lease = await db
            .CodeReviewExecutionLeases.TagWithOperationCallSite(
                "code_review_execution_lease.release_find"
            )
            .FirstOrDefaultAsync(row => row.LeaseId == leaseId, cancellationToken);

        if (lease is null)
        {
            return;
        }

        db.CodeReviewExecutionLeases.Remove(lease);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> DeleteExpiredAsync(
        string organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var expired = await db
            .CodeReviewExecutionLeases.TagWithOperationCallSite(
                "code_review_execution_lease.delete_expired"
            )
            .Where(lease => lease.OrganizationId == organizationId && lease.ExpiresAtUtc <= now)
            .ToArrayAsync(cancellationToken);

        if (expired.Length == 0)
        {
            return 0;
        }

        db.CodeReviewExecutionLeases.RemoveRange(expired);

        await db.SaveChangesAsync(cancellationToken);
        return expired.Length;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static Task CommitIfOwnedAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken
    ) => transaction is null ? Task.CompletedTask : transaction.CommitAsync(cancellationToken);

    private async ValueTask<IDbContextTransaction?> BeginTransactionIfNeededAsync(
        CancellationToken cancellationToken
    ) =>
        db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "Prepared Postgres transaction for code-review execution lease acquisition. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, OwnsTransaction={OwnsTransaction}"
    )]
    private static partial void LogLeaseTransactionReady(
        Microsoft.Extensions.Logging.ILogger logger,
        string organizationId,
        string codeReviewId,
        bool ownsTransaction
    );

    [LoggerMessage(
        EventId = 3403,
        Level = LogLevel.Information,
        Message = "Acquired Postgres advisory lock for code-review execution capacity. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogLeaseAdvisoryLockAcquired(
        Microsoft.Extensions.Logging.ILogger logger,
        string organizationId,
        string codeReviewId
    );

    [LoggerMessage(
        EventId = 3405,
        Level = LogLevel.Information,
        Message = "Loaded live code-review execution lease slots. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, LiveSlotCount={LiveSlotCount}, MaxConcurrentReviews={MaxConcurrentReviews}"
    )]
    private static partial void LogLiveExecutionLeasesLoaded(
        Microsoft.Extensions.Logging.ILogger logger,
        string organizationId,
        string codeReviewId,
        int liveSlotCount,
        int maxConcurrentReviews
    );

    [LoggerMessage(
        EventId = 3407,
        Level = LogLevel.Information,
        Message = "Completed Postgres code-review execution lease acquisition. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, Outcome={Outcome}, LeaseId={LeaseId}, SlotIndex={SlotIndex}"
    )]
    private static partial void LogLeaseAcquisitionCompleted(
        Microsoft.Extensions.Logging.ILogger logger,
        string organizationId,
        string codeReviewId,
        CodeReviewExecutionLeaseOutcome outcome,
        string? leaseId,
        int? slotIndex
    );

    [LoggerMessage(
        EventId = 3408,
        Level = LogLevel.Warning,
        Message = "Code-review execution lease insert hit a unique constraint. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, LeaseId={LeaseId}, SlotIndex={SlotIndex}"
    )]
    private static partial void LogLeaseUniqueViolation(
        Microsoft.Extensions.Logging.ILogger logger,
        Exception exception,
        string organizationId,
        string codeReviewId,
        string leaseId,
        int slotIndex
    );
}
