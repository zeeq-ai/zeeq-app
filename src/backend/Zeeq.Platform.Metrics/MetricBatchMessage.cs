using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Paramore.Brighter;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Cross-organization batch of metric samples bound for <c>zeeq_metric_events</c>.
/// </summary>
/// <remarks>
/// <see cref="ISystemMessage" /> + <see cref="LowPriorityMessage" />: each sample
/// carries its own <c>organization_id</c> as row data, so the batch needs no
/// tenant routing identity and deliberately bypasses tenant-tier bucket fan-out —
/// same reasoning as <c>PrivateRepositorySyncRequested</c>. Metrics are
/// loss-tolerant (NFR-4); the low-priority marker keeps this off the fast lanes.
/// </remarks>
[ConfigurePublisher<LowPriorityMessage>("metrics.batch")]
public sealed class MetricBatchMessage : Event, ISystemMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public MetricBatchMessage()
        : base(Id.Random()) { }

    /// <summary>Samples to persist in one write-store round trip.</summary>
    public required IReadOnlyList<MetricSample> Samples { get; init; }
}
