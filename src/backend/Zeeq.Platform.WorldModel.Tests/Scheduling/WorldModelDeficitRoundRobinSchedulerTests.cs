using Danom;
using Zeeq.Core.Models;
using Zeeq.Platform.WorldModel.Scheduling;

namespace Zeeq.Platform.WorldModel.Tests.Scheduling;

public sealed class WorldModelDeficitRoundRobinSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task LeaseNextAsync_WithPriorityOrg_LeasesTargetsWhileDeficitAllows()
    {
        // Guards the DRR budget invariant: a priority organization receives one lane quantum,
        // leases each queued target it can afford, and carries the unspent deficit forward.
        // Arrange one priority lane with three target groups. The configured policy grants 12 points;
        // costs 5 and 4 fit, while the final cost of 6 must remain pending after spending 9.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Priority, bucket: 0)
        );
        await AddTargetAsync(store, "org_priority", "target-a", OrganizationTier.Priority, cost: 5);
        await AddTargetAsync(store, "org_priority", "target-b", OrganizationTier.Priority, cost: 4);
        await AddTargetAsync(store, "org_priority", "target-c", OrganizationTier.Priority, cost: 6);
        var scheduler = await SchedulerAsync(store);

        // Act: run one worker tick with a valid lease request and enough result capacity for all
        // target groups that the organization's current deficit can afford.
        var leases = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );

        // Observe the returned work and persisted queue state together: both the remaining budget
        // and the one unaffordable target must be visible after the tick.
        var state = store.GetOrganizationQueueState(lane, "org_priority");
        await Assert
            .That(leases.Select(item => item.WorkItem.TargetId))
            .IsEquivalentTo(["target-a", "target-b"]);
        await Assert.That(state.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(3);
        await Assert
            .That(state.Match(some: item => item.ActiveTargetCount, none: () => -1))
            .IsEqualTo(1);
    }

    [Test]
    public async Task LeaseNextAsync_WithLowTierOrg_WaitsUntilDeficitCanPayForTarget()
    {
        // Guards deficit carry-forward: a low-tier organization waits without losing accrued
        // budget, then leases the target once its deficit covers the target cost.
        // Arrange a low-tier lane whose two-point quantum cannot pay for the one target costing 3;
        // the target therefore requires two scheduling ticks before it can be leased.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Low, bucket: 0)
        );
        await AddTargetAsync(store, "org_low", "target-a", OrganizationTier.Low, cost: 3);
        var scheduler = await SchedulerAsync(store);

        // Act on the first tick: accrue the low-tier quantum without removing the unaffordable
        // target from the pending queue.
        var firstPass = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );
        var firstState = store.GetOrganizationQueueState(lane, "org_low");

        // Act on the second tick: add another two points, which brings the carried deficit to 4
        // and makes the three-point target affordable.
        var secondPass = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );
        var secondState = store.GetOrganizationQueueState(lane, "org_low");

        // The first pass must be empty while the second pass consumes the target and leaves one
        // point of deficit for future work.
        await Assert.That(firstPass).IsEmpty();
        await Assert
            .That(firstState.Match(some: item => item.Deficit, none: () => -1))
            .IsEqualTo(2);
        await Assert
            .That(secondPass.Select(item => item.WorkItem.TargetId))
            .IsEquivalentTo(["target-a"]);
        await Assert
            .That(secondState.Match(some: item => item.Deficit, none: () => -1))
            .IsEqualTo(1);
        await Assert
            .That(secondState.Match(some: item => item.ActiveTargetCount, none: () => -1))
            .IsEqualTo(0);
    }

    [Test]
    public async Task LeaseNextAsync_WithMultipleOrganizations_AppliesDeficitPerOrganization()
    {
        // Guards organization isolation: each active organization earns and spends its own
        // deficit, so one organization's target costs cannot consume another's scheduling budget.
        // Arrange two organizations in one default lane. The six-point default quantum exactly
        // pays for org_a's three two-point targets and leaves org_b's budget independent.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        await AddTargetAsync(store, "org_a", "target-a2", OrganizationTier.Default, cost: 2);
        await AddTargetAsync(store, "org_a", "target-a3", OrganizationTier.Default, cost: 2);
        await AddTargetAsync(store, "org_b", "target-b1", OrganizationTier.Default, cost: 2);
        var scheduler = await SchedulerAsync(store);

        // Act: run one tick that visits both organizations, allowing each store transition to
        // refill and spend only the state belonging to that organization.
        var leases = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );

        // Read each organization's state separately so the assertions distinguish independent
        // budgets from one shared lane-level budget.
        var orgAState = store.GetOrganizationQueueState(lane, "org_a");
        var orgBState = store.GetOrganizationQueueState(lane, "org_b");
        await Assert
            .That(leases.Select(item => item.WorkItem.TargetId))
            .IsEquivalentTo(new[] { "target-a1", "target-a2", "target-a3", "target-b1" });
        await Assert.That(orgAState.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(0);
        await Assert.That(orgBState.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(4);
    }

    [Test]
    public async Task AddPendingWork_WithDuplicateTarget_MergesIntoOneLeaseableTargetGroup()
    {
        // Guards the target-scoped dedupe boundary: repeated ingestion for the same target updates
        // the pending target projection instead of creating independent leaseable work groups.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 3);
        var scheduler = await SchedulerAsync(store);

        var leases = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );
        var state = store.GetOrganizationQueueState(lane, "org_a");

        await Assert.That(leases).Count().IsEqualTo(1);
        await Assert.That(leases[0].WorkItem.TargetId).IsEqualTo("target-a1");
        await Assert.That(leases[0].WorkItem.EventCount).IsEqualTo(5);
        await Assert.That(leases[0].WorkItem.EstimatedCost).IsEqualTo(5);
        await Assert
            .That(state.Match(some: item => item.ActiveTargetCount, none: () => -1))
            .IsEqualTo(0);
        await Assert.That(state.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(1);
    }

    [Test]
    public async Task LeaseNextAsync_WithInvalidDirectLeaseRequest_PreservesPendingTarget()
    {
        // Guards the non-destructive validation invariant: invalid lease data must not dequeue
        // pending work or advance scheduling state, so a later valid request can still claim the target.
        // Arrange one pending target and intentionally bypass the lease factory with an empty
        // owner id; the scheduler must revalidate direct public-record construction at its boundary.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        var scheduler = await SchedulerAsync(store);

        // Act first with invalid lease data. Validation must fail before the target is dequeued.
        var invalidLeaseResult = await scheduler.LeaseNextAsync(
            lane,
            new WorldModelLeaseRequest("", Now.AddMinutes(5)),
            maxWorkItems: 10,
            CancellationToken.None
        );
        var stateAfterInvalidLease = store.GetOrganizationQueueState(lane, "org_a");

        // Retry with a valid request immediately afterward; successful leasing proves the failed
        // request left the pending target claimable.
        var validLeases = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );

        // Check both sides of the contract: the invalid request reports its validation error, and
        // the scheduling state is unchanged before the subsequent valid request claims the target.
        await Assert.That(invalidLeaseResult.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Lease owner id is required.");
        await Assert
            .That(stateAfterInvalidLease.Match(some: item => item.Deficit, none: () => -1))
            .IsEqualTo(0);
        await Assert
            .That(
                stateAfterInvalidLease.Match(
                    some: item => item.LastVisitedAtUtc.DefaultValue(DateTimeOffset.MinValue),
                    none: () => DateTimeOffset.MaxValue
                )
            )
            .IsEqualTo(DateTimeOffset.MinValue);
        await Assert
            .That(
                stateAfterInvalidLease.Match(some: item => item.ActiveTargetCount, none: () => -1)
            )
            .IsEqualTo(1);
        await Assert
            .That(validLeases.Select(item => item.WorkItem.TargetId))
            .IsEquivalentTo(["target-a1"]);
    }

    [Test]
    public async Task LeaseNextAsync_WithInvalidDirectLeaseRequestAndNoActiveOrganizations_ReturnsError()
    {
        // Guards boundary validation consistency: invalid scheduler inputs fail even when the
        // active organization list is empty and no store transition would otherwise revalidate them.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        var scheduler = await SchedulerAsync(store);

        var invalidLeaseResult = await scheduler.LeaseNextAsync(
            lane,
            new WorldModelLeaseRequest("", Now.AddMinutes(5)),
            maxWorkItems: 10,
            CancellationToken.None
        );

        await Assert.That(invalidLeaseResult.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Lease owner id is required.");
    }

    [Test]
    public async Task LeaseNextAsync_WithExpiredLeaseRequest_ReturnsErrorWithoutMutatingQueue()
    {
        // Guards temporal lease validation: a worker cannot claim work with a lease that is already
        // expired, and the failed request must leave the queue state untouched.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        var scheduler = await SchedulerAsync(store);

        var expiredLeaseResult = await scheduler.LeaseNextAsync(
            lane,
            new WorldModelLeaseRequest("worker-1", Now.AddTicks(-1)),
            maxWorkItems: 10,
            CancellationToken.None
        );
        var stateAfterExpiredLease = store.GetOrganizationQueueState(lane, "org_a");

        await Assert.That(expiredLeaseResult.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Lease expiry must be in the future.");
        await Assert
            .That(stateAfterExpiredLease.Match(some: item => item.Deficit, none: () => -1))
            .IsEqualTo(0);
        await Assert
            .That(
                stateAfterExpiredLease.Match(
                    some: item => item.LastVisitedAtUtc.DefaultValue(DateTimeOffset.MinValue),
                    none: () => DateTimeOffset.MaxValue
                )
            )
            .IsEqualTo(DateTimeOffset.MinValue);
        await Assert
            .That(
                stateAfterExpiredLease.Match(some: item => item.ActiveTargetCount, none: () => -1)
            )
            .IsEqualTo(1);
    }

    [Test]
    public async Task LeaseNextAsync_WithInvalidDirectLane_ReturnsErrorBeforeQueryingStore()
    {
        // Guards scheduler boundary validation: direct public-record construction cannot bypass
        // lane invariants and make the result depend on store contents.
        var store = new InMemoryWorldModelPendingWorkStore();
        var scheduler = await SchedulerAsync(store);

        var invalidLaneResult = await scheduler.LeaseNextAsync(
            new WorldModelSchedulerLane(OrganizationTier.Default, Bucket: -1),
            await LeaseAsync(),
            maxWorkItems: 10,
            CancellationToken.None
        );

        await Assert.That(invalidLaneResult.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Bucket must be zero or greater.");
    }

    [Test]
    public async Task LeaseNextAsync_WhenOrganizationScanLimitOverflows_ReturnsErrorWithoutAllocation()
    {
        // Guards the allocation invariant: huge requested batch sizes must fail in the checked
        // scan-limit calculation rather than driving eager list allocation.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        var scheduler = new WorldModelDeficitRoundRobinScheduler(
            store,
            await AssertOkAsync(WorldModelTierSchedulePolicy.Create(organizationScanMultiplier: 2)),
            new FixedTimeProvider(Now)
        );

        var result = await scheduler.LeaseNextAsync(
            lane,
            await LeaseAsync(),
            maxWorkItems: int.MaxValue,
            CancellationToken.None
        );

        await Assert.That(result.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Organization scan limit overflowed.");
    }

    [Test]
    public async Task LeaseNextAsync_WithBoundedOrganizationScan_RotatesAcrossActiveOrganizations()
    {
        // Guards the central DRR scan invariant: bounded scans advance LastVisitedAtUtc so
        // successive ticks rotate across still-active organizations instead of retrying the same
        // unaffordable queue forever.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 7);
        await AddTargetAsync(store, "org_b", "target-b1", OrganizationTier.Default, cost: 7);
        var scheduler = new WorldModelDeficitRoundRobinScheduler(
            store,
            await AssertOkAsync(WorldModelTierSchedulePolicy.Create(organizationScanMultiplier: 1)),
            new FixedTimeProvider(Now)
        );

        // Act with capacity for one target and scan breadth of one organization. The target cost is
        // deliberately above the default quantum, so each visited organization remains active.
        var firstPass = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 1,
                CancellationToken.None
            )
        );
        var secondPass = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 1,
                CancellationToken.None
            )
        );

        var orgAState = store.GetOrganizationQueueState(lane, "org_a");
        var orgBState = store.GetOrganizationQueueState(lane, "org_b");

        await Assert.That(firstPass).IsEmpty();
        await Assert.That(secondPass).IsEmpty();
        await Assert.That(orgAState.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(6);
        await Assert.That(orgBState.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(6);
        await Assert
            .That(
                orgAState.Match(
                    some: item => item.LastVisitedAtUtc.DefaultValue(DateTimeOffset.MinValue),
                    none: () => DateTimeOffset.MinValue
                )
            )
            .IsNotEqualTo(DateTimeOffset.MinValue);
        await Assert
            .That(
                orgBState.Match(
                    some: item => item.LastVisitedAtUtc.DefaultValue(DateTimeOffset.MinValue),
                    none: () => DateTimeOffset.MinValue
                )
            )
            .IsNotEqualTo(DateTimeOffset.MinValue);
    }

    [Test]
    public async Task LeaseNextAsync_WhenDeficitWouldOverflow_SaturatesUntilMaximumCostTargetCanRun()
    {
        // Guards maximum-cost progress: a valid int.MaxValue target can eventually become
        // affordable instead of permanently erroring when carried deficit reaches the int ceiling.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Priority, bucket: 0)
        );
        var target = await AssertOkAsync(
            WorldModelTargetWorkItem.Create(
                "org_a",
                WorldModelWorkConsumer.Curator,
                "target-a1",
                OrganizationTier.Priority,
                bucket: 0,
                eventCount: int.MaxValue,
                estimatedCost: int.MaxValue,
                oldestEventAtUtc: Now.AddDays(-1),
                newestEventAtUtc: Now
            )
        );
        await AssertOkAsync(store.AddPendingWork(target));
        var scheduler = new WorldModelDeficitRoundRobinScheduler(
            store,
            await AssertOkAsync(
                WorldModelTierSchedulePolicy.Create(priorityQuantum: int.MaxValue - 1)
            ),
            new FixedTimeProvider(Now)
        );

        var firstPass = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );
        var secondPass = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );
        var state = store.GetOrganizationQueueState(lane, "org_a");

        await Assert.That(firstPass).IsEmpty();
        await Assert
            .That(secondPass.Select(item => item.WorkItem.TargetId))
            .IsEquivalentTo(["target-a1"]);
        await Assert.That(state.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(0);
    }

    [Test]
    public async Task LeaseNextAsync_WhenMiddleOrganizationFails_ContinuesToLaterOrganizations()
    {
        // Guards best-effort liveness: one organization failure must not hide already committed
        // leases or prevent later organizations in the bounded scan from being considered.
        // Arrange an inner store with three organizations. The wrapper delegates org_a and org_c
        // normally, but injects a failure only when the scheduler reaches org_b.
        var innerStore = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AddTargetAsync(innerStore, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        await AddTargetAsync(innerStore, "org_b", "target-b1", OrganizationTier.Default, cost: 2);
        await AddTargetAsync(innerStore, "org_c", "target-c1", OrganizationTier.Default, cost: 2);
        var failingStore = new FailingOrganizationStore(innerStore, "org_b");
        var scheduler = new WorldModelDeficitRoundRobinScheduler(
            failingStore,
            await AssertOkAsync(WorldModelTierSchedulePolicy.Create()),
            new FixedTimeProvider(Now)
        );

        // Act: run one tick; deterministic organization ordering lets org_a commit, org_b fail,
        // and org_c prove the scheduler continues past the failed transition.
        var leases = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );

        var orgAState = innerStore.GetOrganizationQueueState(lane, "org_a");
        var orgBState = innerStore.GetOrganizationQueueState(lane, "org_b");
        var orgCState = innerStore.GetOrganizationQueueState(lane, "org_c");

        // The caller receives committed leases around the failed organization, and the invocation
        // log proves the injected org_b failure was actually reached.
        await Assert
            .That(leases.Select(item => item.WorkItem.TargetId))
            .IsEquivalentTo(["target-a1", "target-c1"]);
        await Assert
            .That(failingStore.AttemptedOrganizationIds)
            .IsEquivalentTo(["org_a", "org_b", "org_c"]);
        await Assert
            .That(orgAState.Match(some: item => item.ActiveTargetCount, none: () => -1))
            .IsEqualTo(0);
        await Assert
            .That(orgBState.Match(some: item => item.ActiveTargetCount, none: () => -1))
            .IsEqualTo(1);
        await Assert
            .That(orgCState.Match(some: item => item.ActiveTargetCount, none: () => -1))
            .IsEqualTo(0);
    }

    [Test]
    public async Task LeaseNextAsync_WhenSuccessfulTransitionLeasesNoWorkAndLaterOrganizationFails_ReturnsEmptySuccess()
    {
        // Guards the partial-success contract: an organization can complete its transition without
        // leasing work, and that still means a later failure is not an all-organizations failure.
        var innerStore = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AddTargetAsync(innerStore, "org_a", "target-a1", OrganizationTier.Default, cost: 7);
        await AddTargetAsync(innerStore, "org_b", "target-b1", OrganizationTier.Default, cost: 2);
        var failingStore = new FailingOrganizationStore(innerStore, "org_b");
        var scheduler = new WorldModelDeficitRoundRobinScheduler(
            failingStore,
            await AssertOkAsync(WorldModelTierSchedulePolicy.Create()),
            new FixedTimeProvider(Now)
        );

        // org_a is visited and accrues deficit, but its target remains unaffordable. org_b then
        // fails, so the overall result should be an empty success rather than a total-failure error.
        var leases = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 10,
                CancellationToken.None
            )
        );
        var orgAState = innerStore.GetOrganizationQueueState(lane, "org_a");

        await Assert.That(leases).IsEmpty();
        await Assert.That(failingStore.AttemptedOrganizationIds).IsEquivalentTo(["org_a", "org_b"]);
        await Assert.That(orgAState.Match(some: item => item.Deficit, none: () => -1)).IsEqualTo(6);
    }

    [Test]
    public async Task AddPendingWork_WithInvalidDirectWorkItem_ReturnsErrorWithoutActiveState()
    {
        // Guards non-destructive enqueue validation: direct public-record construction must not
        // leave pending work behind when it fails validation before state creation.
        var store = new InMemoryWorldModelPendingWorkStore();
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );

        var addResult = store.AddPendingWork(
            new WorldModelTargetWorkItem(
                "",
                WorldModelWorkConsumer.Curator,
                "target-a1",
                OrganizationTier.Default,
                Bucket: 0,
                EventCount: 1,
                EstimatedCost: 1,
                OldestEventAtUtc: Now.AddMinutes(-1),
                NewestEventAtUtc: Now
            )
        );
        var activeOrganizations = await AssertOkAsync(
            await store.ListActiveOrganizationsAsync(
                lane,
                maxOrganizations: 10,
                CancellationToken.None
            )
        );

        await Assert.That(addResult.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Organization id is required.");
        await Assert.That(activeOrganizations).IsEmpty();
    }

    [Test]
    public async Task ReleaseLeaseAsync_RequeuesTargetForAnotherWorker()
    {
        var store = new InMemoryWorldModelPendingWorkStore();
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        var scheduler = await SchedulerAsync(store);
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        var firstLease = (
            await AssertOkAsync(
                await scheduler.LeaseNextAsync(
                    lane,
                    await LeaseAsync(),
                    maxWorkItems: 1,
                    CancellationToken.None
                )
            )
        ).Single();

        await AssertOkAsync(await store.ReleaseLeaseAsync(firstLease, CancellationToken.None));
        var secondLease = (
            await AssertOkAsync(
                await scheduler.LeaseNextAsync(
                    lane,
                    await LeaseAsync(),
                    maxWorkItems: 1,
                    CancellationToken.None
                )
            )
        ).Single();

        await Assert.That(secondLease.WorkItem.TargetId).IsEqualTo("target-a1");
        await Assert.That(secondLease.LeaseId).IsNotEqualTo(firstLease.LeaseId);
    }

    [Test]
    public async Task AddPendingWork_WhenLeasedTargetChangesLane_RejectsUntilCompletion()
    {
        var store = new InMemoryWorldModelPendingWorkStore();
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        var scheduler = await SchedulerAsync(store);
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        var lease = (
            await AssertOkAsync(
                await scheduler.LeaseNextAsync(
                    lane,
                    await LeaseAsync(),
                    maxWorkItems: 1,
                    CancellationToken.None
                )
            )
        ).Single();
        var rerouted = await AssertOkAsync(
            WorldModelTargetWorkItem.Create(
                "org_a",
                WorldModelWorkConsumer.Curator,
                "target-a1",
                OrganizationTier.Default,
                bucket: 1,
                eventCount: 1,
                estimatedCost: 1,
                oldestEventAtUtc: Now,
                newestEventAtUtc: Now
            )
        );

        var result = store.AddPendingWork(rerouted);

        await Assert.That(result.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Target already exists in a different scheduler lane.");
        await AssertOkAsync(await store.CompleteLeaseAsync(lease, CancellationToken.None));
        await AssertOkAsync(store.AddPendingWork(rerouted));
    }

    [Test]
    public async Task ReclaimExpiredLeasesAsync_RequeuesOnlyExpiredTargets()
    {
        var store = new InMemoryWorldModelPendingWorkStore();
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        var scheduler = await SchedulerAsync(store);
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 1,
                CancellationToken.None
            )
        );

        var reclaimed = await AssertOkAsync(
            await store.ReclaimExpiredLeasesAsync(
                Now.AddMinutes(6),
                maxLeases: 10,
                CancellationToken.None
            )
        );
        var next = await AssertOkAsync(
            await scheduler.LeaseNextAsync(
                lane,
                await LeaseAsync(),
                maxWorkItems: 1,
                CancellationToken.None
            )
        );

        await Assert.That(reclaimed).IsEqualTo(1);
        await Assert.That(next).HasSingleItem();
    }

    [Test]
    public async Task RenewLeaseAsync_WhenExpiryDoesNotMoveForward_ReturnsError()
    {
        var store = new InMemoryWorldModelPendingWorkStore();
        await AddTargetAsync(store, "org_a", "target-a1", OrganizationTier.Default, cost: 2);
        var scheduler = await SchedulerAsync(store);
        var lane = await AssertOkAsync(
            WorldModelSchedulerLane.Create(OrganizationTier.Default, bucket: 0)
        );
        var lease = (
            await AssertOkAsync(
                await scheduler.LeaseNextAsync(
                    lane,
                    await LeaseAsync(),
                    maxWorkItems: 1,
                    CancellationToken.None
                )
            )
        ).Single();

        var result = await store.RenewLeaseAsync(lease, lease.ExpiresAtUtc, CancellationToken.None);

        await Assert.That(result.TryGetError(out var error)).IsTrue();
        await Assert.That(error).IsEqualTo("Lease expiration must move forward.");
    }

    private static async Task<WorldModelDeficitRoundRobinScheduler> SchedulerAsync(
        InMemoryWorldModelPendingWorkStore store
    ) =>
        new(
            store,
            await AssertOkAsync(WorldModelTierSchedulePolicy.Create()),
            new FixedTimeProvider(Now)
        );

    private static Task<WorldModelLeaseRequest> LeaseAsync() =>
        AssertOkAsync(WorldModelLeaseRequest.Create("worker-1", Now.AddMinutes(5)));

    private static async Task AddTargetAsync(
        InMemoryWorldModelPendingWorkStore store,
        string organizationId,
        string targetId,
        OrganizationTier tier,
        int cost
    )
    {
        var target = await AssertOkAsync(
            WorldModelTargetWorkItem.Create(
                organizationId,
                WorldModelWorkConsumer.Curator,
                targetId,
                tier,
                bucket: 0,
                eventCount: cost,
                estimatedCost: cost,
                oldestEventAtUtc: Now.AddMinutes(-cost),
                newestEventAtUtc: Now
            )
        );

        await AssertOkAsync(store.AddPendingWork(target));
    }

    private static async Task<T> AssertOkAsync<T>(Result<T, string> result)
    {
        await Assert.That(result.TryGet(out var value)).IsTrue();

        return value!;
    }

    private sealed class FailingOrganizationStore(
        IWorldModelPendingWorkStore inner,
        string failingOrganizationId
    ) : IWorldModelPendingWorkStore
    {
        public List<string> AttemptedOrganizationIds { get; } = [];

        public Task<Result<Unit, string>> EnqueueAsync(
            WorldModelTargetWorkItem item,
            CancellationToken cancellationToken
        ) => inner.EnqueueAsync(item, cancellationToken);

        public Task<
            Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>
        > ListActiveOrganizationsAsync(
            WorldModelSchedulerLane lane,
            int maxOrganizations,
            CancellationToken cancellationToken
        ) => inner.ListActiveOrganizationsAsync(lane, maxOrganizations, cancellationToken);

        public Task<Result<WorldModelOrganizationScheduleResult, string>> LeaseForOrganizationAsync(
            WorldModelOrganizationQueueState state,
            int quantum,
            WorldModelLeaseRequest lease,
            int maxWorkItems,
            DateTimeOffset visitedAtUtc,
            CancellationToken cancellationToken
        )
        {
            AttemptedOrganizationIds.Add(state.OrganizationId);

            if (state.OrganizationId == failingOrganizationId)
            {
                return Task.FromResult(
                    Result<WorldModelOrganizationScheduleResult, string>.Error(
                        "Injected organization failure."
                    )
                );
            }

            return inner.LeaseForOrganizationAsync(
                state,
                quantum,
                lease,
                maxWorkItems,
                visitedAtUtc,
                cancellationToken
            );
        }

        public Task<Result<Unit, string>> RenewLeaseAsync(
            WorldModelWorkLease lease,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken
        ) => inner.RenewLeaseAsync(lease, expiresAtUtc, cancellationToken);

        public Task<Result<Unit, string>> CompleteLeaseAsync(
            WorldModelWorkLease lease,
            CancellationToken cancellationToken
        ) => inner.CompleteLeaseAsync(lease, cancellationToken);

        public Task<Result<Unit, string>> ReleaseLeaseAsync(
            WorldModelWorkLease lease,
            CancellationToken cancellationToken
        ) => inner.ReleaseLeaseAsync(lease, cancellationToken);

        public Task<Result<int, string>> ReclaimExpiredLeasesAsync(
            DateTimeOffset expiredAtUtc,
            int maxLeases,
            CancellationToken cancellationToken
        ) => inner.ReclaimExpiredLeasesAsync(expiredAtUtc, maxLeases, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
