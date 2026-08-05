using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Claimed target-scoped work item returned to a polling worker.
/// </summary>
/// <remarks>
/// A lease is permission to process one target group. Workers should treat it as temporary:
/// storage renews it during long-running work and finalizes it only if the same owner still holds
/// the lease and the target aggregate remains at the captured revision.
/// </remarks>
public sealed record WorldModelWorkLease(
    Guid LeaseId,
    string OwnerId,
    DateTimeOffset ExpiresAtUtc,
    long AggregateRevision,
    WorldModelTargetWorkItem WorkItem
)
{
    /// <summary>
    /// Validates and creates a claimed work lease.
    /// </summary>
    /// <param name="leaseId">Unique identifier for this lease claim.</param>
    /// <param name="ownerId">Stable worker identifier that owns the claim.</param>
    /// <param name="expiresAtUtc">UTC time when the claim should become eligible again.</param>
    /// <param name="aggregateRevision">Target aggregate generation captured by the claim.</param>
    /// <param name="workItem">Target-scoped work claimed by the lease.</param>
    public static Result<WorldModelWorkLease, string> Create(
        Guid leaseId,
        string ownerId,
        DateTimeOffset expiresAtUtc,
        long aggregateRevision,
        WorldModelTargetWorkItem workItem
    )
    {
        if (leaseId == Guid.Empty)
        {
            return Result<WorldModelWorkLease, string>.Error("Lease id is required.");
        }

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return Result<WorldModelWorkLease, string>.Error("Lease owner id is required.");
        }

        if (aggregateRevision < 1)
        {
            return Result<WorldModelWorkLease, string>.Error(
                "Aggregate revision must be greater than zero."
            );
        }

        return Result<WorldModelWorkLease, string>.Ok(
            new(leaseId, ownerId.Trim(), expiresAtUtc, aggregateRevision, workItem)
        );
    }
}
