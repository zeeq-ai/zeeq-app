using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.WorldModel;

/// <summary>
/// Persisted deficit-round-robin state for one organization scheduler lane.
/// </summary>
internal sealed class WorldModelSchedulerQueueStateRow
{
    public required string OrganizationId { get; set; }
    public required OrganizationTier Tier { get; set; }
    public required int Bucket { get; set; }
    public int Deficit { get; set; }
    public int ActiveTargetCount { get; set; }
    public DateTimeOffset? LastVisitedAtUtc { get; set; }
}
