using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Weighted deficit round-robin scheduler for target-scoped world model work.
/// </summary>
/// <remarks>
/// Deficit is tracked per organization inside a scheduler lane. The lane scope
/// lets distributed workers own independent tier-and-bucket routes while still
/// applying organization fairness within each lane.
///
/// One call represents one worker tick for one lane. The scheduler does not move across lanes;
/// a hosting worker is responsible for choosing which lane to tick next. Inside the lane, every
/// active organization receives the lane tier's quantum and can lease multiple target groups while
/// its accumulated deficit covers their estimated cost.
/// </remarks>
public sealed class WorldModelDeficitRoundRobinScheduler(
    IWorldModelPendingWorkStore store,
    WorldModelTierSchedulePolicy policy,
    TimeProvider timeProvider
) : IWorldModelScheduler
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<WorldModelWorkLease>, string>> LeaseNextAsync(
        WorldModelSchedulerLane lane,
        WorldModelLeaseRequest lease,
        int maxWorkItems,
        CancellationToken cancellationToken
    )
    {
        var laneResult = WorldModelSchedulerLane.Create(lane.Tier, lane.Bucket);
        if (laneResult.TryGetError(out var laneError))
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(laneError);
        }

        if (!laneResult.TryGet(out var validatedLane))
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                "Scheduler lane did not return a value."
            );
        }

        var leaseResult = WorldModelLeaseRequest.Create(lease.OwnerId, lease.ExpiresAtUtc);
        if (leaseResult.TryGetError(out var leaseError))
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(leaseError);
        }

        if (!leaseResult.TryGet(out var validatedLease))
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                "Lease request did not return a value."
            );
        }

        if (validatedLease.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                "Lease expiry must be in the future."
            );
        }

        if (maxWorkItems < 1)
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                "Max work items must be greater than zero."
            );
        }

        int maxOrganizations;
        try
        {
            maxOrganizations = checked(maxWorkItems * policy.OrganizationScanMultiplier);
        }
        catch (OverflowException)
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                "Organization scan limit overflowed."
            );
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                "Scheduling tick was canceled."
            );
        }

        var leases = new List<WorldModelWorkLease>();
        string? firstScheduleError = null;
        var hadSuccessfulTransition = false;
        var statesResult = await store.ListActiveOrganizationsAsync(
            validatedLane,
            maxOrganizations,
            cancellationToken
        );
        if (statesResult.TryGetError(out var statesError))
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(statesError);
        }

        if (!statesResult.TryGet(out var states))
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                "Active organization query did not return a value."
            );
        }

        foreach (var originalState in states)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (leases.Count > 0)
                {
                    break;
                }

                return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                    "Scheduling tick was canceled."
                );
            }

            // Tier matters once per active organization per lane tick: it controls the refill rate
            // for the organization's deficit, not the order of individual target rows.
            var quantumResult = policy.GetQuantum(originalState.Lane.Tier);
            if (quantumResult.TryGetError(out var quantumError))
            {
                return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(quantumError);
            }

            if (!quantumResult.TryGet(out var quantum))
            {
                return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                    "Tier quantum did not return a value."
                );
            }

            var remainingCapacity = maxWorkItems - leases.Count;
            var scheduleResult = await store.LeaseForOrganizationAsync(
                originalState,
                quantum,
                validatedLease,
                remainingCapacity,
                timeProvider.GetUtcNow(),
                cancellationToken
            );
            if (scheduleResult.TryGetError(out var scheduleError))
            {
                firstScheduleError ??= scheduleError;

                // NOTE: A single organization transition can fail after earlier organizations
                // committed leases. Keep scanning so a broken queue does not lead every tick and
                // starve later organizations; report the first error only if the tick leases no work.
                continue;
            }

            if (!scheduleResult.TryGet(out var scheduledOrganization))
            {
                return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(
                    "Organization scheduling transition did not return a value."
                );
            }

            hadSuccessfulTransition = true;
            leases.AddRange(scheduledOrganization.Leases);

            if (leases.Count == maxWorkItems)
            {
                break;
            }
        }

        if (leases.Count == 0 && firstScheduleError is not null && !hadSuccessfulTransition)
        {
            return Result<IReadOnlyList<WorldModelWorkLease>, string>.Error(firstScheduleError);
        }

        // NOTE: A successful organization transition can legitimately produce no leases when its
        // next target is still unaffordable. That still counts as lane progress because deficit and
        // LastVisitedAtUtc advanced, so mixed empty-success plus failure returns Ok([]).
        return Result<IReadOnlyList<WorldModelWorkLease>, string>.Ok(leases);
    }
}
