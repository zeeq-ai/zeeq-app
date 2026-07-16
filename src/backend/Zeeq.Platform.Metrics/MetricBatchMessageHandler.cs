using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Persists a <see cref="MetricBatchMessage" /> into <c>zeeq_metric_events</c> in one round trip.
/// </summary>
/// <remarks>
/// <see cref="ISystemMessage" /> single channel with one performer: at the
/// pipeline's flush cadence (a batch every ~2s at most) one writer is ample, and
/// a single writer keeps insert ordering simple. Runs only where consumers are
/// registered (the worker pool) — Brighter's messaging role gates that, so no
/// extra process check is needed here.
/// </remarks>
[ConfigureConsumer<MetricBatchMessage>(
    "metrics.batch.handler",
    noOfPerformers: 1,
    bufferSize: 4,
    visibleTimeoutSeconds: 60
)]
public sealed class MetricBatchMessageHandler(
    IDeadLetterWriter deadLetterWriter,
    IMetricEventStore store
) : ZeeqMessageHandler<MetricBatchMessage>(deadLetterWriter)
{
    /// <inheritdoc />
    protected override async Task<MetricBatchMessage> HandleMessageAsync(
        MetricBatchMessage message,
        CancellationToken cancellationToken
    )
    {
        await store.AppendAsync(message.Samples, cancellationToken);

        return message;
    }
}
