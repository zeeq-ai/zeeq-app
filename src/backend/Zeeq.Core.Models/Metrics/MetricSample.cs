namespace Zeeq.Core.Models;

/// <summary>
/// A single captured measurement in flight between the MeterListener and the
/// <see cref="MetricEvent" /> row it becomes.
/// </summary>
/// <remarks>
/// Field-for-field mirror of <see cref="MetricEvent" /> minus the database
/// identity. It is the payload serialized inside the batch message on the way to
/// the write store; null promoted fields are omitted in serialization, keeping a
/// full 1,000-sample batch well under the transport limit.
///
/// Lives in Core.Models (rather than the platform pipeline project) so both the
/// domain store interface (<see cref="IMetricEventStore" />) and the platform
/// batch message can share it without a Core.Models → platform dependency cycle.
/// </remarks>
/// <param name="OrganizationId">Owning organization; required — the capture rule drops samples without it.</param>
/// <param name="TeamId">Optional team scope.</param>
/// <param name="MetricType">Instrument name verbatim.</param>
/// <param name="MetricValue">Recorded value (1 for counters, the measured amount for histograms).</param>
/// <param name="UserEmail">Promoted <c>user</c> tag.</param>
/// <param name="ToolName">Promoted <c>tool_name</c> tag.</param>
/// <param name="RepositoryId">Promoted <c>repository_id</c> tag.</param>
/// <param name="Library">Promoted <c>library</c> tag.</param>
/// <param name="Facet">Promoted <c>facet</c> tag.</param>
/// <param name="Tags">Residual (non-promoted) tags; null or empty when none.</param>
/// <param name="CapturedAtUtc">Capture timestamp; becomes the partition key.</param>
public sealed record MetricSample(
    string OrganizationId,
    string? TeamId,
    string MetricType,
    double MetricValue,
    string? UserEmail,
    string? ToolName,
    string? RepositoryId,
    string? Library,
    string? Facet,
    Dictionary<string, string>? Tags,
    DateTimeOffset CapturedAtUtc
);
