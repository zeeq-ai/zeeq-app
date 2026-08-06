using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Storage contract used by the world model scheduler to discover and lease target-scoped work.
/// </summary>
/// <remarks>
/// The scheduler is intentionally storage-neutral. It only sees lane-scoped organization queues
/// and asks the store to perform the full per-organization scheduling transition. Implementations
/// own target aggregation, lease concurrency, and durable deficit updates. Methods return
/// <see cref="Result{T, TError}"/> for expected storage or validation failures.
///
/// NOTE: This contract intentionally keeps producer, scheduler, and lease lifecycle operations on
/// one storage boundary because they share aggregation, transaction, and locking invariants.
/// </remarks>
public interface IWorldModelPendingWorkStore
{
    /// <summary>
    /// Adds or merges pending work for one consumer-scoped target.
    /// </summary>
    /// <remarks>
    /// Consumer is part of the target identity: two consumers may independently queue the same
    /// target id without sharing aggregate or lease state.
    /// </remarks>
    Task<Result<Unit, string>> EnqueueAsync(
        WorldModelTargetWorkItem item,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists organizations with pending target work in a scheduler lane.
    /// </summary>
    /// <remarks>
    /// A polling worker calls this once for the lane it is ticking. The returned states are
    /// organization-level flows for deficit round-robin. Target groups are intentionally not fetched
    /// here; they are claimed by <see cref="LeaseForOrganizationAsync"/> so queue mutation and deficit
    /// persistence stay in one store-owned transition.
    /// </remarks>
    /// <param name="lane">Tier-and-bucket lane currently being ticked by the polling worker.</param>
    /// <param name="maxOrganizations">Maximum number of active organization queues to inspect.</param>
    /// <param name="cancellationToken">Cancellation token for the polling tick.</param>
    Task<
        Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>
    > ListActiveOrganizationsAsync(
        WorldModelSchedulerLane lane,
        int maxOrganizations,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Applies one atomic DRR transition for an organization in a lane.
    /// </summary>
    /// <remarks>
    /// The store owns this transition so it can refill deficit, choose target rows, claim leases,
    /// and persist the resulting queue state in one transaction.
    /// This is the single source of truth for both lease creation and organization queue-state
    /// persistence. The scheduler does not separately peek target rows, claim leases, or save deficit
    /// state.
    ///
    /// Implementations must serialize this transition per <c>(lane, organizationId)</c>. Workers may
    /// race on the same lane snapshot; the store boundary prevents duplicate target leases and
    /// avoids applying multiple deficit refills to one organization transition.
    /// </remarks>
    /// <param name="state">Current DRR state for the organization being considered.</param>
    /// <param name="quantum">Tier-weighted deficit to add before selecting target groups.</param>
    /// <param name="lease">Worker lease metadata to apply to claimed target groups.</param>
    /// <param name="maxWorkItems">Remaining capacity in the caller's lane tick.</param>
    /// <param name="visitedAtUtc">UTC timestamp to persist for this organization visit.</param>
    /// <param name="cancellationToken">Cancellation token for the polling tick.</param>
    Task<Result<WorldModelOrganizationScheduleResult, string>> LeaseForOrganizationAsync(
        WorldModelOrganizationQueueState state,
        int quantum,
        WorldModelLeaseRequest lease,
        int maxWorkItems,
        DateTimeOffset visitedAtUtc,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Extends a lease while it is still owned by the requesting worker.
    /// </summary>
    /// <remarks>The new expiry must be later than the lease's current persisted expiry.</remarks>
    Task<Result<Unit, string>> RenewLeaseAsync(
        WorldModelWorkLease lease,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Acknowledges successful processing of a leased target.
    /// </summary>
    /// <remarks>
    /// Completion must not discard events merged into the target after it was leased. Such work
    /// remains pending and becomes immediately eligible for another lease.
    /// </remarks>
    Task<Result<Unit, string>> CompleteLeaseAsync(
        WorldModelWorkLease lease,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Releases a lease so its target can be scheduled again immediately.
    /// </summary>
    Task<Result<Unit, string>> ReleaseLeaseAsync(
        WorldModelWorkLease lease,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Releases abandoned leases whose expiry is at or before the supplied time.
    /// </summary>
    /// <remarks>Concurrent reclaimers must not release the same lease twice.</remarks>
    Task<Result<int, string>> ReclaimExpiredLeasesAsync(
        DateTimeOffset expiredAtUtc,
        int maxLeases,
        CancellationToken cancellationToken
    );
}
