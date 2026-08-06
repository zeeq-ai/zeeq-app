using System.Data.Common;
using Danom;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Models;
using Zeeq.Platform.WorldModel.Scheduling;

namespace Zeeq.Data.Postgres.WorldModel;

/// <summary>
/// Postgres-backed pending-target projection and lease store for world-model scheduling.
/// </summary>
internal sealed class PostgresWorldModelPendingWorkStore(PostgresDbContext db)
    : IWorldModelPendingWorkStore
{
    // NOTE: Keep lifecycle operations together until a shared transaction and lane-lock helper can
    // preserve the lane-before-target lock order across separate collaborators.
    private const string EnqueueSavepointName = "world_model_scheduler_enqueue";

    public async Task<Result<Unit, string>> EnqueueAsync(
        WorldModelTargetWorkItem item,
        CancellationToken cancellationToken
    )
    {
        var validatedResult = WorldModelTargetWorkItem.Create(
            item.OrganizationId,
            item.Consumer,
            item.TargetId,
            item.Tier,
            item.Bucket,
            item.EventCount,
            item.EstimatedCost,
            item.OldestEventAtUtc,
            item.NewestEventAtUtc
        );
        if (validatedResult.TryGetError(out var validationError))
        {
            return Result<Unit, string>.Error(validationError);
        }

        if (!validatedResult.TryGet(out var validatedItem))
        {
            return Result<Unit, string>.Error("Validated target work item was not available.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result<Unit, string>.Error("Enqueue was canceled.");
        }

        IDbContextTransaction? ambientTransaction = null;
        var savepointCreated = false;
        try
        {
            // Lane creation and target aggregation are one transition: neither row is useful alone.
            await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
            ambientTransaction = transaction is null ? db.Database.CurrentTransaction : null;
            if (ambientTransaction is not null)
            {
                if (!ambientTransaction.SupportsSavepoints)
                {
                    return Result<Unit, string>.Error(
                        "The ambient transaction does not support scheduler enqueue savepoints."
                    );
                }

                await ambientTransaction.CreateSavepointAsync(
                    EnqueueSavepointName,
                    cancellationToken
                );
                savepointCreated = true;
            }

            var tier = WorldModelSchedulerStorageValues.Format(validatedItem.Tier);
            var consumer = WorldModelSchedulerStorageValues.Format(validatedItem.Consumer);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO zeeq.awm_scheduler_queue_state
                    (organization_id, tier, bucket, deficit, active_target_count, last_visited_at_utc)
                VALUES ({validatedItem.OrganizationId}, {tier}, {validatedItem.Bucket}, 0, 0, NULL)
                ON CONFLICT (organization_id, tier, bucket) DO NOTHING
                """,
                cancellationToken
            );

            // Every transition takes the organization lock before target locks. This keeps enqueue,
            // lease, and completion on one lock hierarchy under concurrency.
            var lockedLane = await db
                .Database.SqlQuery<int>(
                    $"""
                    SELECT 1 AS "Value"
                    FROM zeeq.awm_scheduler_queue_state
                    WHERE organization_id = {validatedItem.OrganizationId}
                      AND tier = {tier}
                      AND bucket = {validatedItem.Bucket}
                    FOR UPDATE
                    """
                )
                .TagWithOperationCallSite("world_model.scheduler.lock_enqueue_lane")
                .ToArrayAsync(cancellationToken);
            if (lockedLane is not [_])
            {
                await RollbackEnqueueSavepointAsync(ambientTransaction, savepointCreated);

                return Result<Unit, string>.Error("Scheduler lane was not found after creation.");
            }

            // A consumer-target aggregate never moves lanes. A routing mismatch leaves the existing
            // aggregate untouched and returns no row from the CTE, which rolls back lane creation.
            var counts = await db
                .Database.SqlQuery<int>(
                    $"""
                    WITH upserted AS (
                        INSERT INTO zeeq.awm_scheduler_pending_targets AS target
                            (organization_id, consumer, target_id, tier, bucket, event_count,
                             estimated_cost, oldest_event_at_utc, newest_event_at_utc,
                             aggregate_revision)
                        VALUES
                            ({validatedItem.OrganizationId}, {consumer}, {validatedItem.TargetId},
                             {tier}, {validatedItem.Bucket}, {validatedItem.EventCount},
                             {validatedItem.EstimatedCost}, {validatedItem.OldestEventAtUtc},
                             {validatedItem.NewestEventAtUtc}, 1)
                        ON CONFLICT (organization_id, consumer, target_id) DO UPDATE
                        SET event_count = LEAST(2147483647, target.event_count::bigint + EXCLUDED.event_count)::integer,
                            estimated_cost = LEAST(2147483647, target.estimated_cost::bigint + EXCLUDED.estimated_cost)::integer,
                            oldest_event_at_utc = LEAST(target.oldest_event_at_utc, EXCLUDED.oldest_event_at_utc),
                            newest_event_at_utc = GREATEST(target.newest_event_at_utc, EXCLUDED.newest_event_at_utc),
                            aggregate_revision = target.aggregate_revision + 1
                        WHERE target.tier = EXCLUDED.tier AND target.bucket = EXCLUDED.bucket
                        RETURNING (xmax = 0) AS inserted
                    )
                    UPDATE zeeq.awm_scheduler_queue_state AS state
                    SET active_target_count = state.active_target_count
                        + CASE WHEN (SELECT inserted FROM upserted) THEN 1 ELSE 0 END
                    WHERE state.organization_id = {validatedItem.OrganizationId}
                      AND state.tier = {tier}
                      AND state.bucket = {validatedItem.Bucket}
                      AND EXISTS (SELECT 1 FROM upserted)
                    RETURNING state.active_target_count AS "Value"
                    """
                )
                .TagWithOperationCallSite("world_model.scheduler.enqueue")
                .ToArrayAsync(cancellationToken);
            if (counts.Length != 1)
            {
                await RollbackEnqueueSavepointAsync(ambientTransaction, savepointCreated);

                return Result<Unit, string>.Error(
                    "Target already exists in a different scheduler lane."
                );
            }

            await ReleaseEnqueueSavepointAsync(ambientTransaction, savepointCreated);
            await CommitIfOwnedAsync(transaction, cancellationToken);

            return Result<Unit, string>.Ok(Unit.Value);
        }
        catch (OperationCanceledException)
        {
            await RollbackEnqueueSavepointAsync(ambientTransaction, savepointCreated);
            throw;
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            await RollbackEnqueueSavepointAsync(ambientTransaction, savepointCreated);

            return Result<Unit, string>.Error(exception.Message);
        }
        catch
        {
            await RollbackEnqueueSavepointAsync(ambientTransaction, savepointCreated);
            throw;
        }
    }

    public async Task<
        Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>
    > ListActiveOrganizationsAsync(
        WorldModelSchedulerLane lane,
        int maxOrganizations,
        CancellationToken cancellationToken
    )
    {
        if (maxOrganizations < 1)
        {
            return Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>.Error(
                "Max organizations must be greater than zero."
            );
        }

        try
        {
            var rows = await db
                .WorldModelSchedulerQueueStates.AsNoTracking()
                .TagWithOperationCallSite("world_model.scheduler.list_active_organizations")
                .Where(row =>
                    row.Tier == lane.Tier && row.Bucket == lane.Bucket && row.ActiveTargetCount > 0
                )
                .OrderBy(row => row.LastVisitedAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(row => row.OrganizationId)
                .Take(maxOrganizations)
                .ToArrayAsync(cancellationToken);
            var states = new List<WorldModelOrganizationQueueState>(rows.Length);
            foreach (var row in rows)
            {
                var stateResult = WorldModelOrganizationQueueState.Create(
                    row.OrganizationId,
                    lane,
                    row.Deficit,
                    row.ActiveTargetCount,
                    row.LastVisitedAtUtc is { } visitedAt
                        ? Option.Some(visitedAt)
                        : Option<DateTimeOffset>.NoneValue
                );
                if (!stateResult.TryGet(out var state))
                {
                    return Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>.Error(
                        "Persisted organization queue state is invalid."
                    );
                }

                states.Add(state);
            }

            return Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>.Ok(states);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>.Error(
                exception.Message
            );
        }
    }

    public async Task<
        Result<WorldModelOrganizationScheduleResult, string>
    > LeaseForOrganizationAsync(
        WorldModelOrganizationQueueState state,
        int quantum,
        WorldModelLeaseRequest lease,
        int maxWorkItems,
        DateTimeOffset visitedAtUtc,
        CancellationToken cancellationToken
    )
    {
        if (quantum < 1 || maxWorkItems < 1)
        {
            return Result<WorldModelOrganizationScheduleResult, string>.Error(
                "Quantum and max work items must be greater than zero."
            );
        }

        try
        {
            await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
            var tier = WorldModelSchedulerStorageValues.Format(state.Lane.Tier);
            // The queue-state lock owns this organization's complete DRR transition. Callers may
            // supply stale snapshots, but only the locked deficit is refilled and persisted.
            var lockedRows = await db
                .Database.SqlQuery<LockedQueueState>(
                    $"""
                    SELECT deficit, active_target_count
                    FROM zeeq.awm_scheduler_queue_state
                    WHERE organization_id = {state.OrganizationId}
                      AND tier = {tier}
                      AND bucket = {state.Lane.Bucket}
                    FOR UPDATE
                    """
                )
                .TagWithOperationCallSite("world_model.scheduler.lock_organization")
                .ToArrayAsync(cancellationToken);
            if (lockedRows is not [var locked])
            {
                return Result<WorldModelOrganizationScheduleResult, string>.Error(
                    "Organization queue state was not found."
                );
            }

            var availableDeficit = (int)Math.Min(int.MaxValue, (long)locked.Deficit + quantum);
            // running_cost selects an affordable FIFO prefix. Waiting for locked target rows avoids
            // advancing newer work while enqueue or reclamation updates the head of the queue.
            var claimedRows = await db
                .Database.SqlQuery<ClaimedTarget>(
                    $"""
                    WITH available AS (
                        SELECT organization_id, consumer, target_id, tier, bucket, event_count,
                               estimated_cost, oldest_event_at_utc, newest_event_at_utc,
                               aggregate_revision
                        FROM zeeq.awm_scheduler_pending_targets
                        WHERE organization_id = {state.OrganizationId}
                          AND tier = {tier}
                          AND bucket = {state.Lane.Bucket}
                          AND leased_by IS NULL
                        ORDER BY oldest_event_at_utc, consumer, target_id
                        LIMIT {maxWorkItems}
                        FOR UPDATE
                    ), affordable AS (
                        SELECT *, SUM(estimated_cost) OVER (
                            ORDER BY oldest_event_at_utc, consumer, target_id
                        ) AS running_cost
                        FROM available
                    )
                    UPDATE zeeq.awm_scheduler_pending_targets AS target
                    SET leased_by = {lease.OwnerId},
                        -- Each target gets its own lifecycle token even when claimed in one batch.
                        lease_id = gen_random_uuid(),
                        lease_expires_at_utc = {lease.ExpiresAtUtc}
                    FROM affordable
                    WHERE target.organization_id = affordable.organization_id
                      AND target.consumer = affordable.consumer
                      AND target.target_id = affordable.target_id
                      AND affordable.running_cost <= {availableDeficit}
                    RETURNING target.organization_id,
                              target.consumer,
                              target.target_id,
                              target.tier,
                              target.bucket,
                              target.event_count,
                              target.estimated_cost,
                              target.oldest_event_at_utc,
                              target.newest_event_at_utc,
                              target.aggregate_revision,
                              target.lease_id
                    """
                )
                .TagWithOperationCallSite("world_model.scheduler.claim_targets")
                .ToArrayAsync(cancellationToken);
            var spent = claimedRows.Sum(row => row.EstimatedCost);
            var remainingDeficit = availableDeficit - spent;
            var remainingActiveTargetCount = locked.ActiveTargetCount - claimedRows.Length;

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE zeeq.awm_scheduler_queue_state
                SET deficit = {remainingDeficit},
                    active_target_count = {remainingActiveTargetCount},
                    last_visited_at_utc = {visitedAtUtc}
                WHERE organization_id = {state.OrganizationId}
                  AND tier = {tier}
                  AND bucket = {state.Lane.Bucket}
                """,
                cancellationToken
            );

            var leases = new List<WorldModelWorkLease>(claimedRows.Length);
            foreach (var row in claimedRows)
            {
                if (
                    !WorldModelSchedulerStorageValues.TryParseConsumer(
                        row.Consumer,
                        out var consumer
                    )
                    || !WorldModelSchedulerStorageValues.TryParseTier(row.Tier, out var rowTier)
                )
                {
                    return Result<WorldModelOrganizationScheduleResult, string>.Error(
                        "Persisted scheduler target has an invalid enum value."
                    );
                }

                var itemResult = WorldModelTargetWorkItem.Create(
                    row.OrganizationId,
                    consumer,
                    row.TargetId,
                    rowTier,
                    row.Bucket,
                    row.EventCount,
                    row.EstimatedCost,
                    row.OldestEventAtUtc,
                    row.NewestEventAtUtc
                );
                if (!itemResult.TryGet(out var item))
                {
                    return Result<WorldModelOrganizationScheduleResult, string>.Error(
                        "Persisted scheduler target is invalid."
                    );
                }

                var workLeaseResult = WorldModelWorkLease.Create(
                    row.LeaseId,
                    lease.OwnerId,
                    lease.ExpiresAtUtc,
                    row.AggregateRevision,
                    item
                );
                if (!workLeaseResult.TryGet(out var workLease))
                {
                    return Result<WorldModelOrganizationScheduleResult, string>.Error(
                        "Claimed scheduler lease is invalid."
                    );
                }

                leases.Add(workLease);
            }

            var updatedStateResult = WorldModelOrganizationQueueState.Create(
                state.OrganizationId,
                state.Lane,
                remainingDeficit,
                remainingActiveTargetCount,
                Option.Some(visitedAtUtc)
            );
            if (!updatedStateResult.TryGet(out var updatedState))
            {
                return Result<WorldModelOrganizationScheduleResult, string>.Error(
                    "Updated organization queue state is invalid."
                );
            }

            await CommitIfOwnedAsync(transaction, cancellationToken);

            return Result<WorldModelOrganizationScheduleResult, string>.Ok(
                new(updatedState, leases)
            );
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result<WorldModelOrganizationScheduleResult, string>.Error(exception.Message);
        }
    }

    public async Task<Result<Unit, string>> RenewLeaseAsync(
        WorldModelWorkLease lease,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken
    )
    {
        if (expiresAtUtc <= lease.ExpiresAtUtc)
        {
            return Result<Unit, string>.Error("Lease expiration must move forward.");
        }

        try
        {
            var updated = await db
                .WorldModelSchedulerPendingTargets.Where(row =>
                    row.OrganizationId == lease.WorkItem.OrganizationId
                    && row.Consumer == lease.WorkItem.Consumer
                    && row.TargetId == lease.WorkItem.TargetId
                    && row.LeaseId == lease.LeaseId
                    && row.LeasedBy == lease.OwnerId
                    && row.LeaseExpiresAtUtc < expiresAtUtc
                )
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.LeaseExpiresAtUtc, expiresAtUtc),
                    cancellationToken
                );

            return updated == 1
                ? Result<Unit, string>.Ok(Unit.Value)
                : Result<Unit, string>.Error(
                    "Lease is not owned by this worker or expiration did not move forward."
                );
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result<Unit, string>.Error(exception.Message);
        }
    }

    public async Task<Result<Unit, string>> CompleteLeaseAsync(
        WorldModelWorkLease lease,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
            var tier = WorldModelSchedulerStorageValues.Format(lease.WorkItem.Tier);
            // Match lease and enqueue ordering: organization queue state is always locked first.
            var lockedLane = await db
                .Database.SqlQuery<int>(
                    $"""
                    SELECT 1 AS "Value"
                    FROM zeeq.awm_scheduler_queue_state
                    WHERE organization_id = {lease.WorkItem.OrganizationId}
                      AND tier = {tier}
                      AND bucket = {lease.WorkItem.Bucket}
                    FOR UPDATE
                    """
                )
                .TagWithOperationCallSite("world_model.scheduler.lock_completion_lane")
                .ToArrayAsync(cancellationToken);
            if (lockedLane is not [_])
            {
                return Result<Unit, string>.Error("Organization queue state was not found.");
            }

            var matchingLease = db.WorldModelSchedulerPendingTargets.Where(row =>
                row.OrganizationId == lease.WorkItem.OrganizationId
                && row.Consumer == lease.WorkItem.Consumer
                && row.TargetId == lease.WorkItem.TargetId
                && row.Tier == lease.WorkItem.Tier
                && row.Bucket == lease.WorkItem.Bucket
                && row.LeaseId == lease.LeaseId
                && row.LeasedBy == lease.OwnerId
            );
            // Delete only the generation captured by the lease. Enqueue may merge newer events while
            // processing is in flight; that row is retained and made leaseable below.
            var deleted = await matchingLease
                .Where(row => row.AggregateRevision == lease.AggregateRevision)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted == 1)
            {
                await CommitIfOwnedAsync(transaction, cancellationToken);

                return Result<Unit, string>.Ok(Unit.Value);
            }

            var retained = await matchingLease.ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.LeasedBy, (string?)null)
                        .SetProperty(row => row.LeaseId, (Guid?)null)
                        .SetProperty(row => row.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken
            );
            if (retained != 1)
            {
                return Result<Unit, string>.Error("Lease is not owned by this worker.");
            }

            // Enqueue can merge a newer generation into a leased row. Completion makes that
            // retained generation claimable, so it returns to the lane's active count here.
            await db
                .WorldModelSchedulerQueueStates.Where(row =>
                    row.OrganizationId == lease.WorkItem.OrganizationId
                    && row.Tier == lease.WorkItem.Tier
                    && row.Bucket == lease.WorkItem.Bucket
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters.SetProperty(
                            row => row.ActiveTargetCount,
                            row => row.ActiveTargetCount + 1
                        ),
                    cancellationToken
                );

            await CommitIfOwnedAsync(transaction, cancellationToken);

            return Result<Unit, string>.Ok(Unit.Value);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result<Unit, string>.Error(exception.Message);
        }
    }

    public async Task<Result<Unit, string>> ReleaseLeaseAsync(
        WorldModelWorkLease lease,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);
            var tier = WorldModelSchedulerStorageValues.Format(lease.WorkItem.Tier);
            // Releasing a lease changes both target availability and lane discovery. Lock the lane
            // first so those values move atomically under the scheduler's shared lock hierarchy.
            var lockedLane = await db
                .Database.SqlQuery<int>(
                    $"""
                    SELECT 1 AS "Value"
                    FROM zeeq.awm_scheduler_queue_state
                    WHERE organization_id = {lease.WorkItem.OrganizationId}
                      AND tier = {tier}
                      AND bucket = {lease.WorkItem.Bucket}
                    FOR UPDATE
                    """
                )
                .TagWithOperationCallSite("world_model.scheduler.lock_release_lane")
                .ToArrayAsync(cancellationToken);
            if (lockedLane is not [_])
            {
                return Result<Unit, string>.Error("Organization queue state was not found.");
            }

            var updated = await db
                .WorldModelSchedulerPendingTargets.Where(row =>
                    row.OrganizationId == lease.WorkItem.OrganizationId
                    && row.Consumer == lease.WorkItem.Consumer
                    && row.TargetId == lease.WorkItem.TargetId
                    && row.Tier == lease.WorkItem.Tier
                    && row.Bucket == lease.WorkItem.Bucket
                    && row.LeaseId == lease.LeaseId
                    && row.LeasedBy == lease.OwnerId
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(row => row.LeasedBy, (string?)null)
                            .SetProperty(row => row.LeaseId, (Guid?)null)
                            .SetProperty(row => row.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                    cancellationToken
                );
            if (updated != 1)
            {
                return Result<Unit, string>.Error("Lease is not owned by this worker.");
            }

            await db
                .WorldModelSchedulerQueueStates.Where(row =>
                    row.OrganizationId == lease.WorkItem.OrganizationId
                    && row.Tier == lease.WorkItem.Tier
                    && row.Bucket == lease.WorkItem.Bucket
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters.SetProperty(
                            row => row.ActiveTargetCount,
                            row => row.ActiveTargetCount + 1
                        ),
                    cancellationToken
                );
            await CommitIfOwnedAsync(transaction, cancellationToken);

            return Result<Unit, string>.Ok(Unit.Value);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result<Unit, string>.Error(exception.Message);
        }
    }

    public async Task<Result<int, string>> ReclaimExpiredLeasesAsync(
        DateTimeOffset expiredAtUtc,
        int maxLeases,
        CancellationToken cancellationToken
    )
    {
        if (maxLeases < 1)
        {
            return Result<int, string>.Error("Max leases must be greater than zero.");
        }

        try
        {
            // Sweepers first divide organization lanes with SKIP LOCKED, then release target rows.
            // This preserves the lane-before-target lock order used by every lifecycle transition.
            // Materialize instead of using SingleAsync: EF composition would wrap this data-modifying
            // CTE, but PostgreSQL requires a data-modifying CTE at the top level.
            var reclaimedRows = await db
                .Database.SqlQuery<int>(
                    $"""
                    WITH candidate_lanes AS MATERIALIZED (
                        SELECT state.organization_id, state.tier, state.bucket
                        FROM zeeq.awm_scheduler_queue_state AS state
                        WHERE EXISTS (
                            SELECT 1
                            FROM zeeq.awm_scheduler_pending_targets AS target
                            WHERE target.organization_id = state.organization_id
                              AND target.tier = state.tier
                              AND target.bucket = state.bucket
                              AND target.leased_by IS NOT NULL
                              AND target.lease_expires_at_utc <= {expiredAtUtc}
                        )
                        ORDER BY state.organization_id, state.tier, state.bucket
                        LIMIT {maxLeases}
                        FOR UPDATE OF state SKIP LOCKED
                    ), expired AS (
                        SELECT target.organization_id, target.consumer, target.target_id
                        FROM zeeq.awm_scheduler_pending_targets AS target
                        INNER JOIN candidate_lanes AS lane
                            ON lane.organization_id = target.organization_id
                           AND lane.tier = target.tier
                           AND lane.bucket = target.bucket
                        WHERE target.leased_by IS NOT NULL
                          AND target.lease_expires_at_utc <= {expiredAtUtc}
                        ORDER BY target.lease_expires_at_utc,
                                 target.organization_id,
                                 target.consumer,
                                 target.target_id
                        LIMIT {maxLeases}
                        FOR UPDATE OF target
                    ), released AS (
                        UPDATE zeeq.awm_scheduler_pending_targets AS target
                        SET leased_by = NULL, lease_id = NULL, lease_expires_at_utc = NULL
                        FROM expired
                        WHERE target.organization_id = expired.organization_id
                          AND target.consumer = expired.consumer
                          AND target.target_id = expired.target_id
                        RETURNING target.organization_id, target.tier, target.bucket
                    ), released_by_lane AS (
                        SELECT organization_id, tier, bucket, COUNT(*)::integer AS reclaimed
                        FROM released
                        GROUP BY organization_id, tier, bucket
                    ), reactivated AS (
                        UPDATE zeeq.awm_scheduler_queue_state AS state
                        SET active_target_count = state.active_target_count + released.reclaimed
                        FROM released_by_lane AS released
                        WHERE state.organization_id = released.organization_id
                          AND state.tier = released.tier
                          AND state.bucket = released.bucket
                        RETURNING released.reclaimed
                    )
                    SELECT COALESCE(SUM(reclaimed), 0)::integer AS "Value" FROM reactivated
                    """
                )
                .TagWithOperationCallSite("world_model.scheduler.reclaim_expired_leases")
                .ToArrayAsync(cancellationToken);
            if (reclaimedRows is not [var reclaimed])
            {
                return Result<int, string>.Error("Expired lease reclamation returned no count.");
            }

            return Result<int, string>.Ok(reclaimed);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            return Result<int, string>.Error(exception.Message);
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is DbException or DbUpdateException;

    // Tests and higher-level units may already own a transaction. Only commit transactions created
    // here, preserving the caller's rollback boundary.
    private static Task CommitIfOwnedAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken
    ) => transaction is null ? Task.CompletedTask : transaction.CommitAsync(cancellationToken);

    private static async Task RollbackEnqueueSavepointAsync(
        IDbContextTransaction? transaction,
        bool savepointCreated
    )
    {
        if (transaction is null || !savepointCreated)
        {
            return;
        }

        // Cleanup must remain possible after caller cancellation or a failed database command.
        await transaction.RollbackToSavepointAsync(EnqueueSavepointName, CancellationToken.None);
        await transaction.ReleaseSavepointAsync(EnqueueSavepointName, CancellationToken.None);
    }

    private static Task ReleaseEnqueueSavepointAsync(
        IDbContextTransaction? transaction,
        bool savepointCreated
    ) =>
        transaction is null || !savepointCreated
            ? Task.CompletedTask
            : transaction.ReleaseSavepointAsync(EnqueueSavepointName, CancellationToken.None);

    private async ValueTask<IDbContextTransaction?> BeginTransactionIfNeededAsync(
        CancellationToken cancellationToken
    ) =>
        db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private sealed class LockedQueueState
    {
        public int Deficit { get; init; }
        public int ActiveTargetCount { get; init; }
    }

    private sealed class ClaimedTarget
    {
        public required string OrganizationId { get; init; }
        public required string Consumer { get; init; }
        public required string TargetId { get; init; }
        public required string Tier { get; init; }
        public int Bucket { get; init; }
        public int EventCount { get; init; }
        public int EstimatedCost { get; init; }
        public DateTimeOffset OldestEventAtUtc { get; init; }
        public DateTimeOffset NewestEventAtUtc { get; init; }
        public long AggregateRevision { get; init; }
        public Guid LeaseId { get; init; }
    }
}
