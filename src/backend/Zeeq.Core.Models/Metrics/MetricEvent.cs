namespace Zeeq.Core.Models;

/// <summary>
/// One captured metric measurement (a row in <c>zeeq_metric_events</c>).
/// </summary>
/// <remarks>
/// Sparse wide-event shape: only the promoted columns relevant to the row's
/// <see cref="MetricType" /> family are populated (see the metric taxonomy in
/// the metrics pipeline docs); the rest stay null. There are no foreign keys by
/// design — labels are point-in-time snapshots, not referential to current
/// state, and read queries must never JOIN. The table is range-partitioned by
/// <see cref="CreatedAtUtc" />, so the primary key is composite
/// <c>(Id, CreatedAtUtc)</c> exactly like <see cref="CodeReviewRecord" />, since
/// PostgreSQL requires the partition key to participate in the key.
///
/// Metrics are loss-tolerant informational telemetry, not authoritative
/// accounting: the write path may drop measurements under load and rows carry
/// provider-reported values verbatim.
/// </remarks>
public sealed class MetricEvent
{
    /// <summary>Database identity for the measurement row.</summary>
    public long Id { get; set; }

    /// <summary>Owning organization; the partition/shard scope for every query.</summary>
    public required string OrganizationId { get; set; }

    /// <summary>Optional team scope; null for organization-wide measurements.</summary>
    public string? TeamId { get; set; }

    /// <summary>Instrument name verbatim (for example <c>zeeq_tool_call_counter</c>).</summary>
    public required string MetricType { get; set; }

    /// <summary>Recorded value; counter rows use <c>1</c>, histogram rows the measured amount.</summary>
    public double MetricValue { get; set; } = 1;

    /// <summary>Promoted <c>user</c> tag (the authenticated email) when the family carries it.</summary>
    public string? UserEmail { get; set; }

    /// <summary>Promoted <c>tool_name</c> tag when the family carries it.</summary>
    public string? ToolName { get; set; }

    /// <summary>Promoted <c>repository_id</c> tag when the family carries it.</summary>
    public string? RepositoryId { get; set; }

    /// <summary>Promoted <c>library</c> tag when the family carries it.</summary>
    public string? Library { get; set; }

    /// <summary>Promoted <c>facet</c> tag when the family carries it.</summary>
    public string? Facet { get; set; }

    /// <summary>Residual tags that are not promoted to columns, stored as jsonb.</summary>
    public Dictionary<string, string> Tags { get; set; } = [];

    /// <summary>Capture timestamp; the range-partition key.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}
