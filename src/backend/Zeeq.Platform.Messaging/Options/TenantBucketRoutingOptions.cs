namespace Zeeq.Platform.Messaging;

/// <summary>
/// Tenant-tier bucket counts used for stable route generation.
/// </summary>
public sealed class TenantBucketRoutingOptions
{
    /// <summary>
    /// Number of buckets for priority-tier organizations.
    /// </summary>
    public int PriorityBucketCount { get; init; } = 8;

    /// <summary>
    /// Number of buckets for default-tier organizations.
    /// </summary>
    public int DefaultBucketCount { get; init; } = 8;

    /// <summary>
    /// Number of buckets for low-tier organizations.
    /// </summary>
    public int LowBucketCount { get; init; } = 4;

    /// <summary>
    /// Optional note or deployment identifier documenting a bucket-count migration.
    /// </summary>
    public string? BucketCountMigrationPlan { get; init; }
}
