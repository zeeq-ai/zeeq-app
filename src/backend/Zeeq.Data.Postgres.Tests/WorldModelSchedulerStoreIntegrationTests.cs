using Danom;
using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.WorldModel;
using Zeeq.Platform.WorldModel.Scheduling;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Exercises durable world-model scheduler transitions against real Postgres.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/WorldModelSchedulerStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class WorldModelSchedulerStoreIntegrationTests(PgDatabaseFixture postgres)
{
    [Test]
    public async Task EnqueueAsync_MergesByConsumerScopedTarget()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();

        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator, eventCount: 1),
                CancellationToken.None
            )
        );
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator, eventCount: 2),
                CancellationToken.None
            )
        );
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(
                    organizationId,
                    WorldModelWorkConsumer.ClusterIndex,
                    eventCount: 1
                ),
                CancellationToken.None
            )
        );

        var leases = await LeaseAsync(store, organizationId, maxWorkItems: 10);

        await Assert.That(leases).Count().IsEqualTo(2);
        await Assert
            .That(
                leases
                    .Single(lease => lease.WorkItem.Consumer == WorldModelWorkConsumer.Curator)
                    .WorkItem.EventCount
            )
            .IsEqualTo(3);
    }

    [Test]
    public async Task EnqueueAsync_WithDirectRecord_PersistsCanonicalIdentity()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        var directRecord = new WorldModelTargetWorkItem(
            $"  {organizationId}  ",
            WorldModelWorkConsumer.Curator,
            "  target-canonical  ",
            OrganizationTier.Default,
            Bucket: 0,
            EventCount: 1,
            EstimatedCost: 1,
            OldestEventAtUtc: UtcNow.AddMinutes(-1),
            NewestEventAtUtc: UtcNow
        );

        await AssertOkAsync(await store.EnqueueAsync(directRecord, CancellationToken.None));
        var lease = (await LeaseAsync(store, organizationId)).Single();

        await Assert.That(lease.WorkItem.OrganizationId).IsEqualTo(organizationId);
        await Assert.That(lease.WorkItem.TargetId).IsEqualTo("target-canonical");
    }

    [Test]
    public async Task EnqueueAsync_WithLaneMismatchInAmbientTransaction_RollsBackLaneCreation()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var mismatch = await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator, bucket: 1),
                CancellationToken.None
            );

            await Assert.That(mismatch.TryGetError(out _)).IsTrue();
            await transaction.CommitAsync();
        }

        var orphanLaneCount = await context
            .Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM zeeq.awm_scheduler_queue_state
                WHERE organization_id = {organizationId} AND bucket = 1
                """
            )
            .SingleAsync();

        await Assert.That(orphanLaneCount).IsEqualTo(0);
    }

    [Test]
    public async Task LeaseForOrganizationAsync_WithConcurrentWorkers_DoesNotDoubleClaim()
    {
        var organizationId = NewOrganizationId();
        await using (var seedContext = postgres.CreateContext())
        {
            var seedStore = new PostgresWorldModelPendingWorkStore(seedContext);
            await AssertOkAsync(
                await seedStore.EnqueueAsync(
                    await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                    CancellationToken.None
                )
            );
        }

        await using var firstContext = postgres.CreateContext();
        await using var secondContext = postgres.CreateContext();
        var firstStore = new PostgresWorldModelPendingWorkStore(firstContext);
        var secondStore = new PostgresWorldModelPendingWorkStore(secondContext);
        var state = await ActiveStateAsync(firstStore, organizationId);
        var first = LeaseForStateAsync(firstStore, state, ownerId: "worker-1");
        var second = LeaseForStateAsync(secondStore, state, ownerId: "worker-2");

        var results = await Task.WhenAll(first, second);
        var leases = results.SelectMany(result => result).ToArray();

        await Assert.That(leases).HasSingleItem();
        await Assert.That(leases[0].WorkItem.TargetId).IsEqualTo("target-1");
    }

    [Test]
    public async Task LeaseForOrganizationAsync_WithOnlyLeasedWork_DeactivatesOrganization()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );

        await LeaseAsync(store, organizationId);
        var activeStates = await AssertOkAsync(
            await store.ListActiveOrganizationsAsync(Lane, 10, CancellationToken.None)
        );
        var activeTargetCount = await context
            .WorldModelSchedulerQueueStates.Where(row =>
                row.OrganizationId == organizationId
                && row.Tier == Lane.Tier
                && row.Bucket == Lane.Bucket
            )
            .Select(row => row.ActiveTargetCount)
            .SingleAsync();

        await Assert
            .That(activeStates.Any(state => state.OrganizationId == organizationId))
            .IsFalse();
        await Assert.That(activeTargetCount).IsEqualTo(0);
    }

    [Test]
    public async Task LeaseForOrganizationAsync_WhenOldestTargetIsLocked_WaitsForFifoHead()
    {
        var organizationId = NewOrganizationId();
        await using (var seedContext = postgres.CreateContext())
        {
            var seedStore = new PostgresWorldModelPendingWorkStore(seedContext);
            await AssertOkAsync(
                await seedStore.EnqueueAsync(
                    await TargetAsync(
                        organizationId,
                        WorldModelWorkConsumer.Curator,
                        targetId: "target-old",
                        oldestEventAtUtc: UtcNow.AddMinutes(-2)
                    ),
                    CancellationToken.None
                )
            );
            await AssertOkAsync(
                await seedStore.EnqueueAsync(
                    await TargetAsync(
                        organizationId,
                        WorldModelWorkConsumer.Curator,
                        targetId: "target-new",
                        oldestEventAtUtc: UtcNow.AddMinutes(-1)
                    ),
                    CancellationToken.None
                )
            );
        }

        await using var lockContext = postgres.CreateContext();
        await using var lockTransaction = await lockContext.Database.BeginTransactionAsync();
        await lockContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE zeeq.awm_scheduler_pending_targets
            SET estimated_cost = estimated_cost
            WHERE organization_id = {organizationId} AND target_id = {"target-old"}
            """
        );

        await using var leaseContext = postgres.CreateContext();
        var leaseStore = new PostgresWorldModelPendingWorkStore(leaseContext);
        var state = await ActiveStateAsync(leaseStore, organizationId);
        var leaseTask = LeaseForStateAsync(leaseStore, state, ownerId: "worker-fifo");

        // NOTE: Keep this as a coarse integration guard rather than coupling the fixture to
        // pg_stat_activity visibility and PostgreSQL's reporting of lock-wait internals.
        await Task.Delay(100);
        await Assert.That(leaseTask.IsCompleted).IsFalse();

        await lockTransaction.CommitAsync();
        var lease = (await leaseTask).Single();

        await Assert.That(lease.WorkItem.TargetId).IsEqualTo("target-old");
    }

    [Test]
    public async Task CompleteLeaseAsync_WhenQueueStateIsLocked_DoesNotLockTargetFirst()
    {
        var organizationId = NewOrganizationId();
        WorldModelWorkLease lease;
        await using (var seedContext = postgres.CreateContext())
        {
            var seedStore = new PostgresWorldModelPendingWorkStore(seedContext);
            await AssertOkAsync(
                await seedStore.EnqueueAsync(
                    await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                    CancellationToken.None
                )
            );
            lease = (await LeaseAsync(seedStore, organizationId)).Single();
        }

        await using var queueLockContext = postgres.CreateContext();
        await using var queueLockTransaction =
            await queueLockContext.Database.BeginTransactionAsync();
        var tier = OrganizationTier.Default.ToString();
        await queueLockContext
            .Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM zeeq.awm_scheduler_queue_state
                WHERE organization_id = {organizationId} AND tier = {tier} AND bucket = 0
                FOR UPDATE
                """
            )
            .ToArrayAsync();

        await using var completionContext = postgres.CreateContext();
        var completionStore = new PostgresWorldModelPendingWorkStore(completionContext);
        var completionTask = completionStore.CompleteLeaseAsync(lease, CancellationToken.None);
        // NOTE: The NOWAIT probe below is the behavioral assertion; this delay only gives the
        // competing command an opportunity to reach its queue-state lock.
        await Task.Delay(100);
        await Assert.That(completionTask.IsCompleted).IsFalse();

        await using (var targetLockContext = postgres.CreateContext())
        await using (
            var targetLockTransaction = await targetLockContext.Database.BeginTransactionAsync()
        )
        {
            var targetRows = await targetLockContext
                .Database.SqlQuery<int>(
                    $"""
                    SELECT 1 AS "Value"
                    FROM zeeq.awm_scheduler_pending_targets
                    WHERE organization_id = {organizationId} AND target_id = {lease.WorkItem.TargetId}
                    FOR UPDATE NOWAIT
                    """
                )
                .ToArrayAsync();

            await Assert.That(targetRows).HasSingleItem();
            await targetLockTransaction.CommitAsync();
        }

        await queueLockTransaction.CommitAsync();
        await AssertOkAsync(await completionTask);
    }

    [Test]
    public async Task ReleaseLeaseAsync_MakesTargetImmediatelyLeaseable()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );
        var first = (await LeaseAsync(store, organizationId)).Single();

        await AssertOkAsync(await store.ReleaseLeaseAsync(first, CancellationToken.None));
        var second = (await LeaseAsync(store, organizationId)).Single();

        await Assert.That(second.LeaseId).IsNotEqualTo(first.LeaseId);
    }

    [Test]
    public async Task CompleteLeaseAsync_WhenNewWorkArrived_RetainsMergedTarget()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );
        var first = (await LeaseAsync(store, organizationId)).Single();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );

        await AssertOkAsync(await store.CompleteLeaseAsync(first, CancellationToken.None));
        var second = (await LeaseAsync(store, organizationId)).Single();

        await Assert.That(second.WorkItem.EventCount).IsEqualTo(2);
    }

    [Test]
    public async Task CompleteLeaseAsync_WhenSaturatedAggregateChanged_RetainsNewGeneration()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(
                    organizationId,
                    WorldModelWorkConsumer.Curator,
                    eventCount: int.MaxValue,
                    estimatedCost: 1
                ),
                CancellationToken.None
            )
        );
        var first = (await LeaseAsync(store, organizationId)).Single();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator, estimatedCost: 1),
                CancellationToken.None
            )
        );

        await AssertOkAsync(await store.CompleteLeaseAsync(first, CancellationToken.None));
        var second = (await LeaseAsync(store, organizationId)).Single();

        await Assert.That(second.WorkItem.EventCount).IsEqualTo(int.MaxValue);
        await Assert.That(second.AggregateRevision).IsEqualTo(first.AggregateRevision + 1);
    }

    [Test]
    public async Task RenewLeaseAsync_WhenExpiryDoesNotMoveForward_ReturnsError()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );
        var lease = (await LeaseAsync(store, organizationId)).Single();

        var result = await store.RenewLeaseAsync(
            lease,
            lease.ExpiresAtUtc.AddMinutes(-1),
            CancellationToken.None
        );

        await Assert.That(result.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Lease expiration must move forward.");
    }

    [Test]
    public async Task CompleteLeaseAsync_WithoutNewWork_RemovesTarget()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );
        var lease = (await LeaseAsync(store, organizationId)).Single();

        await AssertOkAsync(await store.CompleteLeaseAsync(lease, CancellationToken.None));
        var states = await AssertOkAsync(
            await store.ListActiveOrganizationsAsync(
                Lane,
                maxOrganizations: 100,
                CancellationToken.None
            )
        );

        await Assert.That(states.Any(state => state.OrganizationId == organizationId)).IsFalse();
    }

    [Test]
    public async Task CompleteLeaseAsync_WithMismatchedLane_DoesNotMutateEitherLane()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(
                    organizationId,
                    WorldModelWorkConsumer.Curator,
                    targetId: "target-lane-1",
                    bucket: 1
                ),
                CancellationToken.None
            )
        );
        var lease = (await LeaseAsync(store, organizationId)).Single();
        var mismatchedLease = lease with { WorkItem = lease.WorkItem with { Bucket = 1 } };

        var mismatch = await store.CompleteLeaseAsync(mismatchedLease, CancellationToken.None);
        var laneOneCount = await context
            .Database.SqlQuery<int>(
                $"""
                SELECT active_target_count AS "Value"
                FROM zeeq.awm_scheduler_queue_state
                WHERE organization_id = {organizationId} AND bucket = 1
                """
            )
            .SingleAsync();

        await Assert.That(mismatch.TryGetError(out _)).IsTrue();
        await Assert.That(laneOneCount).IsEqualTo(1);
        await AssertOkAsync(await store.CompleteLeaseAsync(lease, CancellationToken.None));
    }

    [Test]
    public async Task ReleaseLeaseAsync_WithMismatchedLane_DoesNotMutateEitherLane()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(
                    organizationId,
                    WorldModelWorkConsumer.Curator,
                    targetId: "target-lane-1",
                    bucket: 1
                ),
                CancellationToken.None
            )
        );
        var lease = (await LeaseAsync(store, organizationId)).Single();
        var mismatchedLease = lease with { WorkItem = lease.WorkItem with { Bucket = 1 } };

        var mismatch = await store.ReleaseLeaseAsync(mismatchedLease, CancellationToken.None);
        var laneOneCount = await context
            .Database.SqlQuery<int>(
                $"""
                SELECT active_target_count AS "Value"
                FROM zeeq.awm_scheduler_queue_state
                WHERE organization_id = {organizationId} AND bucket = 1
                """
            )
            .SingleAsync();

        await Assert.That(mismatch.TryGetError(out _)).IsTrue();
        await Assert.That(laneOneCount).IsEqualTo(1);
        await AssertOkAsync(await store.ReleaseLeaseAsync(lease, CancellationToken.None));
    }

    [Test]
    public async Task ReclaimExpiredLeasesAsync_ReleasesOnlyExpiredLease()
    {
        await using var context = postgres.CreateContext();
        var store = new PostgresWorldModelPendingWorkStore(context);
        var organizationId = NewOrganizationId();
        await AssertOkAsync(
            await store.EnqueueAsync(
                await TargetAsync(organizationId, WorldModelWorkConsumer.Curator),
                CancellationToken.None
            )
        );
        await LeaseAsync(store, organizationId, expiresAtUtc: UtcNow.AddMinutes(5));

        var reclaimed = await AssertOkAsync(
            await store.ReclaimExpiredLeasesAsync(
                UtcNow.AddMinutes(6),
                maxLeases: 10,
                CancellationToken.None
            )
        );
        var next = await LeaseAsync(store, organizationId);

        await Assert.That(reclaimed).IsEqualTo(1);
        await Assert.That(next).HasSingleItem();
    }

    private static readonly DateTimeOffset UtcNow = new DateTimeOffset(
        2026,
        8,
        5,
        12,
        0,
        0,
        TimeSpan.Zero
    ).TruncateToPostgresPrecision();
    private static readonly WorldModelSchedulerLane Lane = new(OrganizationTier.Default, 0);

    private static async Task<WorldModelTargetWorkItem> TargetAsync(
        string organizationId,
        WorldModelWorkConsumer consumer,
        string targetId = "target-1",
        int eventCount = 1,
        int? estimatedCost = null,
        DateTimeOffset? oldestEventAtUtc = null,
        int bucket = 0
    ) =>
        await AssertOkAsync(
            WorldModelTargetWorkItem.Create(
                organizationId,
                consumer,
                targetId,
                OrganizationTier.Default,
                bucket,
                eventCount,
                estimatedCost: estimatedCost ?? eventCount,
                oldestEventAtUtc: oldestEventAtUtc ?? UtcNow.AddMinutes(-1),
                newestEventAtUtc: UtcNow
            )
        );

    private static async Task<WorldModelOrganizationQueueState> ActiveStateAsync(
        PostgresWorldModelPendingWorkStore store,
        string organizationId
    )
    {
        var states = await AssertOkAsync(
            await store.ListActiveOrganizationsAsync(
                Lane,
                maxOrganizations: 100,
                CancellationToken.None
            )
        );

        return states.Single(state => state.OrganizationId == organizationId);
    }

    private static async Task<IReadOnlyList<WorldModelWorkLease>> LeaseAsync(
        PostgresWorldModelPendingWorkStore store,
        string organizationId,
        int maxWorkItems = 1,
        DateTimeOffset? expiresAtUtc = null
    ) =>
        await LeaseForStateAsync(
            store,
            await ActiveStateAsync(store, organizationId),
            ownerId: $"worker-{Guid.CreateVersion7()}",
            maxWorkItems,
            expiresAtUtc
        );

    private static async Task<IReadOnlyList<WorldModelWorkLease>> LeaseForStateAsync(
        PostgresWorldModelPendingWorkStore store,
        WorldModelOrganizationQueueState state,
        string ownerId,
        int maxWorkItems = 1,
        DateTimeOffset? expiresAtUtc = null
    )
    {
        // Most leases deliberately outlive every test sweep. The expiry test opts into a short
        // lease so its global reclaimer cannot release work owned by tests running in parallel.
        var leaseRequest = await AssertOkAsync(
            WorldModelLeaseRequest.Create(ownerId, expiresAtUtc ?? UtcNow.AddDays(1))
        );
        var result = await AssertOkAsync(
            await store.LeaseForOrganizationAsync(
                state,
                quantum: 100,
                leaseRequest,
                maxWorkItems,
                UtcNow,
                CancellationToken.None
            )
        );

        return result.Leases;
    }

    private static string NewOrganizationId() => $"org-wm-{Guid.CreateVersion7()}";

    private static async Task<T> AssertOkAsync<T>(Result<T, string> result)
    {
        if (result.TryGetError(out var error))
        {
            throw new InvalidOperationException(error);
        }

        await Assert.That(result.TryGet(out var value)).IsTrue();

        return value!;
    }
}
