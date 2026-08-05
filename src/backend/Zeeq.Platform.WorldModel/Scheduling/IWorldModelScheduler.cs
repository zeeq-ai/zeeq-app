using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Selects target-scoped world model work using organization-level fairness.
/// </summary>
/// <remarks>
/// The scheduler operates inside one <see cref="WorldModelSchedulerLane"/> per call. A worker tick
/// chooses a lane, then this service walks active organizations in that lane, adds tier-weighted
/// deficit, and leases target groups while each organization can pay their estimated cost.
/// </remarks>
public interface IWorldModelScheduler
{
    /// <summary>
    /// Leases the next batch of target groups for a scheduler lane.
    /// </summary>
    /// <remarks>
    /// The returned leases identify <c>(organizationId, targetId)</c> groups, not raw events. The
    /// eventual worker will use the lease to load and process all unprocessed events for that
    /// target together, which preserves the dedupe and LLM-pass boundary.
    ///
    /// Scheduling is best-effort across organizations in one lane tick. If some organization
    /// transitions fail after other leases have already committed, the scheduler returns the
    /// committed leases so workers can process claimed work. If every attempted organization fails
    /// and no leases commit, the first failure is returned.
    /// </remarks>
    /// <param name="lane">Tier-and-bucket lane to schedule for this tick.</param>
    /// <param name="lease">Worker lease metadata to apply to all claimed target groups.</param>
    /// <param name="maxWorkItems">Maximum number of target groups to lease in this tick.</param>
    /// <param name="cancellationToken">Cancellation token for the polling tick.</param>
    Task<Result<IReadOnlyList<WorldModelWorkLease>, string>> LeaseNextAsync(
        WorldModelSchedulerLane lane,
        WorldModelLeaseRequest lease,
        int maxWorkItems,
        CancellationToken cancellationToken
    );
}
