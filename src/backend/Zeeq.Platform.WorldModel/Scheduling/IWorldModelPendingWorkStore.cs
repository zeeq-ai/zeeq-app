using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Storage contract used by the world model scheduler to discover and lease target-scoped work.
/// </summary>
/// <remarks>
/// The scheduler is intentionally storage-neutral. A future Postgres implementation can use
/// pending-work tables, row leases, and <c>FOR UPDATE SKIP LOCKED</c> internally. The scheduler
/// only sees lane-scoped organization queues and asks the store to perform the full per-organization
/// scheduling transition. Methods return <see cref="Result{T, TError}"/> for expected storage or
/// validation failures.
/// </remarks>
public interface IWorldModelPendingWorkStore
{
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
    /// The store owns this transition so the future Postgres implementation can refill deficit,
    /// choose target rows, claim leases, and persist the resulting queue state in one transaction.
    /// This is the single source of truth for both lease creation and organization queue-state
    /// persistence. The scheduler does not separately peek target rows, claim leases, or save deficit
    /// state.
    ///
    /// NOTE: A distributed Postgres implementation should serialize this transition per
    /// <c>(lane, organizationId)</c>, likely with row locks or <c>FOR UPDATE SKIP LOCKED</c>, and
    /// claim target rows in the same transaction as the deficit and <c>LastVisitedAtUtc</c> update.
    /// Workers may race on the same lane snapshot; the store boundary must prevent duplicate target
    /// leases and avoid applying multiple deficit refills to the same organization transition.
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
}
