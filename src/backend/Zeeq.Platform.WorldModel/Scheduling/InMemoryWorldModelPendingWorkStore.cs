using System.Collections.Concurrent;
using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// In-memory pending work store used to exercise scheduler behavior without durable storage.
/// </summary>
/// <remarks>
/// This store is a behavioral test double, not a production queue. It models the durable store's
/// boundaries: target groups are queued per organization-lane key, leases claim one target group,
/// and organization deficit is saved separately from raw pending work.
/// </remarks>
public sealed class InMemoryWorldModelPendingWorkStore : IWorldModelPendingWorkStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<OrganizationLaneKey, WorldModelOrganizationQueueState> _states = [];
    private readonly Dictionary<OrganizationLaneKey, Queue<WorldModelTargetWorkItem>> _pending = [];
    private readonly Dictionary<WorldModelTargetKey, WorldModelSchedulerLane> _targetLanes = [];
    private readonly ConcurrentDictionary<Guid, WorldModelWorkLease> _leases = [];

    /// <summary>
    /// Adds pending target work to the in-memory queue.
    /// </summary>
    /// <remarks>
    /// In production, event ingestion should maintain a pending-target projection with an upsert.
    /// This helper plays that role for tests by creating the organization queue state the first
    /// time an organization has work in a lane.
    /// </remarks>
    public Result<Unit, string> AddPendingWork(WorldModelTargetWorkItem item)
    {
        var itemResult = WorldModelTargetWorkItem.Create(
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
        if (itemResult.TryGetError(out var itemError))
        {
            return Result<Unit, string>.Error(itemError);
        }

        if (!itemResult.TryGet(out var validatedItem))
        {
            return Result<Unit, string>.Error("Target work item did not return a value.");
        }

        var laneResult = WorldModelSchedulerLane.Create(validatedItem.Tier, validatedItem.Bucket);
        if (laneResult.TryGetError(out var laneError))
        {
            return Result<Unit, string>.Error(laneError);
        }

        if (!laneResult.TryGet(out var lane))
        {
            return Result<Unit, string>.Error("Scheduler lane did not return a value.");
        }

        var stateResult = WorldModelOrganizationQueueState.Create(
            validatedItem.OrganizationId,
            lane,
            deficit: 0,
            activeTargetCount: 1,
            lastVisitedAtUtc: Option<DateTimeOffset>.NoneValue
        );
        if (stateResult.TryGetError(out var stateError))
        {
            return Result<Unit, string>.Error(stateError);
        }

        if (!stateResult.TryGet(out var initialState))
        {
            return Result<Unit, string>.Error("Organization queue state did not return a value.");
        }

        var key = new OrganizationLaneKey(lane, validatedItem.OrganizationId);

        lock (_gate)
        {
            var targetKey = WorldModelTargetKey.From(validatedItem);
            if (_targetLanes.TryGetValue(targetKey, out var existingLane) && existingLane != lane)
            {
                return Result<Unit, string>.Error(
                    "Target already exists in a different scheduler lane."
                );
            }

            if (!_pending.TryGetValue(key, out var queue))
            {
                queue = new Queue<WorldModelTargetWorkItem>();
                _pending[key] = queue;
            }

            // Validate before mutating so direct record construction cannot leave unreachable work.
            // Queue position is stable when an existing consumer-target aggregate is replaced.
            var upsertResult = UpsertPendingTarget(queue, validatedItem);
            if (upsertResult.TryGetError(out var upsertError))
            {
                return Result<Unit, string>.Error(upsertError);
            }

            // Lane ownership survives dequeue while a lease is active, matching the durable key.
            _targetLanes[targetKey] = lane;
            var activeTargetCount = queue.Count;
            if (_states.TryGetValue(key, out var state))
            {
                _states[key] = state with { ActiveTargetCount = activeTargetCount };

                return Result<Unit, string>.Ok(Unit.Value);
            }

            _states[key] = initialState with { ActiveTargetCount = activeTargetCount };

            return Result<Unit, string>.Ok(Unit.Value);
        }
    }

    /// <inheritdoc />
    public Task<Result<Unit, string>> EnqueueAsync(
        WorldModelTargetWorkItem item,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult(
            cancellationToken.IsCancellationRequested
                ? Result<Unit, string>.Error("Enqueue was canceled.")
                : AddPendingWork(item)
        );

    /// <inheritdoc />
    public Task<
        Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>
    > ListActiveOrganizationsAsync(
        WorldModelSchedulerLane lane,
        int maxOrganizations,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(
                Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>.Error(
                    "Scheduling tick was canceled."
                )
            );
        }

        if (maxOrganizations < 1)
        {
            return Task.FromResult(
                Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>.Error(
                    "Max organizations must be greater than zero."
                )
            );
        }

        lock (_gate)
        {
            IReadOnlyList<WorldModelOrganizationQueueState> states =
            [
                .. _states
                    .Values.Where(state => state.Lane == lane && state.ActiveTargetCount > 0)
                    .OrderBy(state => state.LastVisitedAtUtc.DefaultValue(DateTimeOffset.MinValue))
                    .ThenBy(state => state.OrganizationId, StringComparer.Ordinal)
                    .Take(maxOrganizations),
            ];

            return Task.FromResult(
                Result<IReadOnlyList<WorldModelOrganizationQueueState>, string>.Ok(states)
            );
        }
    }

    /// <inheritdoc />
    public Task<Result<WorldModelOrganizationScheduleResult, string>> LeaseForOrganizationAsync(
        WorldModelOrganizationQueueState state,
        int quantum,
        WorldModelLeaseRequest lease,
        int maxWorkItems,
        DateTimeOffset visitedAtUtc,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(
                Result<WorldModelOrganizationScheduleResult, string>.Error(
                    "Scheduling tick was canceled."
                )
            );
        }

        if (quantum < 1)
        {
            return Task.FromResult(
                Result<WorldModelOrganizationScheduleResult, string>.Error(
                    "Quantum must be greater than zero."
                )
            );
        }

        if (maxWorkItems < 1)
        {
            return Task.FromResult(
                Result<WorldModelOrganizationScheduleResult, string>.Error(
                    "Max work items must be greater than zero."
                )
            );
        }

        var leaseRequestResult = WorldModelLeaseRequest.Create(lease.OwnerId, lease.ExpiresAtUtc);
        if (leaseRequestResult.TryGetError(out var leaseRequestError))
        {
            return Task.FromResult(
                Result<WorldModelOrganizationScheduleResult, string>.Error(leaseRequestError)
            );
        }

        lock (_gate)
        {
            var key = new OrganizationLaneKey(state.Lane, state.OrganizationId);
            if (!_states.TryGetValue(key, out var currentState))
            {
                return Task.FromResult(
                    Result<WorldModelOrganizationScheduleResult, string>.Error(
                        "Organization queue state was not found."
                    )
                );
            }

            if (!_pending.TryGetValue(key, out var queue))
            {
                var emptyState = currentState with
                {
                    ActiveTargetCount = 0,
                    LastVisitedAtUtc = Option.Some(visitedAtUtc),
                };
                _states[key] = emptyState;

                return Task.FromResult(
                    Result<WorldModelOrganizationScheduleResult, string>.Ok(new(emptyState, []))
                );
            }

            var deficit = (int)Math.Min(int.MaxValue, (long)currentState.Deficit + quantum);

            var leases = new List<WorldModelWorkLease>();
            while (leases.Count < maxWorkItems && queue.TryPeek(out var next))
            {
                if (next.EstimatedCost > deficit)
                {
                    break;
                }

                var workLeaseResult = WorldModelWorkLease.Create(
                    Guid.CreateVersion7(),
                    lease.OwnerId,
                    lease.ExpiresAtUtc,
                    aggregateRevision: 1,
                    next
                );
                if (workLeaseResult.TryGetError(out var error))
                {
                    return Task.FromResult(
                        Result<WorldModelOrganizationScheduleResult, string>.Error(error)
                    );
                }

                if (!workLeaseResult.TryGet(out var workLease))
                {
                    return Task.FromResult(
                        Result<WorldModelOrganizationScheduleResult, string>.Error(
                            "Lease creation did not return a value."
                        )
                    );
                }

                // NOTE: Dequeue only after lease metadata is validated. Later iterations reuse the
                // same validated owner and generated nonempty lease ids, so ordinary Result errors
                // are not expected after the first dequeue.
                queue.Dequeue();
                leases.Add(workLease);
                _leases[workLease.LeaseId] = workLease;
                deficit -= workLease.WorkItem.EstimatedCost;
            }

            var updatedState = currentState with
            {
                Deficit = deficit,
                ActiveTargetCount = queue.Count,
                LastVisitedAtUtc = Option.Some(visitedAtUtc),
            };
            _states[key] = updatedState;

            return Task.FromResult(
                Result<WorldModelOrganizationScheduleResult, string>.Ok(new(updatedState, leases))
            );
        }
    }

    /// <summary>
    /// Reads a stored organization queue state for tests and diagnostics.
    /// </summary>
    public Option<WorldModelOrganizationQueueState> GetOrganizationQueueState(
        WorldModelSchedulerLane lane,
        string organizationId
    )
    {
        lock (_gate)
        {
            var key = new OrganizationLaneKey(lane, organizationId);

            return _states.TryGetValue(key, out var state)
                ? Option.Some(state)
                : Option<WorldModelOrganizationQueueState>.NoneValue;
        }
    }

    /// <inheritdoc />
    public Task<Result<Unit, string>> RenewLeaseAsync(
        WorldModelWorkLease lease,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<Unit, string>.Error("Lease renewal was canceled."));
        }

        lock (_gate)
        {
            if (
                !_leases.TryGetValue(lease.LeaseId, out var current)
                || current.OwnerId != lease.OwnerId
            )
            {
                return Task.FromResult(
                    Result<Unit, string>.Error("Lease is not owned by this worker.")
                );
            }

            if (expiresAtUtc <= current.ExpiresAtUtc)
            {
                return Task.FromResult(
                    Result<Unit, string>.Error("Lease expiration must move forward.")
                );
            }

            var renewedResult = WorldModelWorkLease.Create(
                current.LeaseId,
                current.OwnerId,
                expiresAtUtc,
                current.AggregateRevision,
                current.WorkItem
            );
            if (!renewedResult.TryGet(out var renewed))
            {
                return Task.FromResult(Result<Unit, string>.Error("Renewed lease is invalid."));
            }

            _leases[lease.LeaseId] = renewed;

            return Task.FromResult(Result<Unit, string>.Ok(Unit.Value));
        }
    }

    /// <inheritdoc />
    public Task<Result<Unit, string>> CompleteLeaseAsync(
        WorldModelWorkLease lease,
        CancellationToken cancellationToken
    ) => RemoveLeaseAsync(lease, requeue: false, cancellationToken);

    /// <inheritdoc />
    public Task<Result<Unit, string>> ReleaseLeaseAsync(
        WorldModelWorkLease lease,
        CancellationToken cancellationToken
    ) => RemoveLeaseAsync(lease, requeue: true, cancellationToken);

    /// <inheritdoc />
    public Task<Result<int, string>> ReclaimExpiredLeasesAsync(
        DateTimeOffset expiredAtUtc,
        int maxLeases,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<int, string>.Error("Lease reclamation was canceled."));
        }

        if (maxLeases < 1)
        {
            return Task.FromResult(
                Result<int, string>.Error("Max leases must be greater than zero.")
            );
        }

        lock (_gate)
        {
            var expired = _leases
                .Values.Where(lease => lease.ExpiresAtUtc <= expiredAtUtc)
                .OrderBy(lease => lease.ExpiresAtUtc)
                .ThenBy(lease => lease.LeaseId)
                .Take(maxLeases)
                .ToArray();
            foreach (var lease in expired)
            {
                _leases.TryRemove(lease.LeaseId, out _);
                // System.Threading.Lock is recursive; AddPendingWork re-enters the same gate while
                // restoring the aggregate and its organization state as one in-memory transition.
                var addResult = AddPendingWork(lease.WorkItem);
                if (addResult.TryGetError(out var error))
                {
                    return Task.FromResult(Result<int, string>.Error(error));
                }
            }

            return Task.FromResult(Result<int, string>.Ok(expired.Length));
        }
    }

    private sealed record OrganizationLaneKey(WorldModelSchedulerLane Lane, string OrganizationId);

    private readonly record struct WorldModelTargetKey(
        string OrganizationId,
        WorldModelWorkConsumer Consumer,
        string TargetId
    )
    {
        public static WorldModelTargetKey From(WorldModelTargetWorkItem item) =>
            new(item.OrganizationId, item.Consumer, item.TargetId);
    }

    private static Result<Unit, string> UpsertPendingTarget(
        Queue<WorldModelTargetWorkItem> queue,
        WorldModelTargetWorkItem item
    )
    {
        if (
            !queue.Any(pending =>
                pending.Consumer == item.Consumer && pending.TargetId == item.TargetId
            )
        )
        {
            queue.Enqueue(item);

            return Result<Unit, string>.Ok(Unit.Value);
        }

        var pendingItems = queue.ToArray();
        var replacementItems = new List<WorldModelTargetWorkItem>(pendingItems.Length);
        foreach (var pending in pendingItems)
        {
            if (pending.Consumer != item.Consumer || pending.TargetId != item.TargetId)
            {
                replacementItems.Add(pending);
                continue;
            }

            var mergedResult = MergePendingTarget(pending, item);
            if (mergedResult.TryGetError(out var mergeError))
            {
                return Result<Unit, string>.Error(mergeError);
            }

            if (!mergedResult.TryGet(out var merged))
            {
                return Result<Unit, string>.Error(
                    "Merged target work item did not return a value."
                );
            }

            replacementItems.Add(merged);
        }

        queue.Clear();
        foreach (var pending in replacementItems)
        {
            queue.Enqueue(pending);
        }

        return Result<Unit, string>.Ok(Unit.Value);
    }

    private static Result<WorldModelTargetWorkItem, string> MergePendingTarget(
        WorldModelTargetWorkItem current,
        WorldModelTargetWorkItem incoming
    )
    {
        // Saturation keeps repeated aggregation from overflowing into an invalid negative cost.
        var eventCount = (int)
            Math.Min(int.MaxValue, (long)current.EventCount + incoming.EventCount);
        var estimatedCost = (int)
            Math.Min(int.MaxValue, (long)current.EstimatedCost + incoming.EstimatedCost);

        return WorldModelTargetWorkItem.Create(
            current.OrganizationId,
            current.Consumer,
            current.TargetId,
            current.Tier,
            current.Bucket,
            eventCount,
            estimatedCost,
            current.OldestEventAtUtc <= incoming.OldestEventAtUtc
                ? current.OldestEventAtUtc
                : incoming.OldestEventAtUtc,
            current.NewestEventAtUtc >= incoming.NewestEventAtUtc
                ? current.NewestEventAtUtc
                : incoming.NewestEventAtUtc
        );
    }

    private Task<Result<Unit, string>> RemoveLeaseAsync(
        WorldModelWorkLease lease,
        bool requeue,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Result<Unit, string>.Error("Lease operation was canceled."));
        }

        lock (_gate)
        {
            if (
                !_leases.TryGetValue(lease.LeaseId, out var current)
                || current.OwnerId != lease.OwnerId
            )
            {
                return Task.FromResult(
                    Result<Unit, string>.Error("Lease is not owned by this worker.")
                );
            }

            _leases.TryRemove(lease.LeaseId, out _);
            if (requeue)
            {
                // Requeue through the aggregation path so organization state and target identity
                // follow the same rules as newly arrived work.
                var addResult = AddPendingWork(current.WorkItem);
                if (addResult.TryGetError(out var error))
                {
                    return Task.FromResult(Result<Unit, string>.Error(error));
                }
            }
            else
            {
                RemoveLaneOwnershipIfInactive(current.WorkItem);
            }

            return Task.FromResult(Result<Unit, string>.Ok(Unit.Value));
        }
    }

    private void RemoveLaneOwnershipIfInactive(WorldModelTargetWorkItem item)
    {
        var targetKey = WorldModelTargetKey.From(item);
        var lane = new WorldModelSchedulerLane(item.Tier, item.Bucket);
        var organizationLaneKey = new OrganizationLaneKey(lane, item.OrganizationId);
        var hasPending =
            _pending.TryGetValue(organizationLaneKey, out var queue)
            && queue.Any(pending => WorldModelTargetKey.From(pending) == targetKey);
        var hasLease = _leases.Values.Any(lease =>
            WorldModelTargetKey.From(lease.WorkItem) == targetKey
        );

        if (!hasPending && !hasLease)
        {
            _targetLanes.Remove(targetKey);
        }
    }
}
