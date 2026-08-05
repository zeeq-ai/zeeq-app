using Zeeq.Core.Models;
using Zeeq.Platform.WorldModel.Scheduling;

namespace Zeeq.Data.Postgres.WorldModel;

/// <summary>
/// Persisted aggregate and lease state for one consumer-scoped world-model target.
/// </summary>
/// <remarks>
/// Event and cost fields may continue to grow while leased. AggregateRevision advances on every
/// merge so completion can retain concurrently added work even when counters are saturated.
/// </remarks>
internal sealed class WorldModelSchedulerPendingTargetRow
{
    public required string OrganizationId { get; set; }
    public required WorldModelWorkConsumer Consumer { get; set; }
    public required string TargetId { get; set; }
    public required OrganizationTier Tier { get; set; }
    public required int Bucket { get; set; }
    public int EventCount { get; set; }
    public int EstimatedCost { get; set; }
    public DateTimeOffset OldestEventAtUtc { get; set; }
    public DateTimeOffset NewestEventAtUtc { get; set; }
    public long AggregateRevision { get; set; }
    public string? LeasedBy { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
}
