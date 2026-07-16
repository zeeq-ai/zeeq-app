using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Paramore.Brighter;

namespace Zeeq.Platform.Metrics.Tests;

/// <summary>
/// Unit tests for <see cref="MetricBatchMessageHandler" /> — confirms it forwards the batch's
/// samples to the store exactly once, using in-memory fakes (no DB, no Brighter pipeline).
/// </summary>
public sealed class MetricBatchMessageHandlerTests
{
    [Test]
    public async Task HandleAsync_ForwardsBatchSamplesToStoreOnce()
    {
        var store = new RecordingMetricEventStore();
        var handler = new MetricBatchMessageHandler(new NoOpDeadLetterWriter(), store);
        var samples = new List<MetricSample> { Sample("org_a"), Sample("org_b") };
        var message = new MetricBatchMessage { Samples = samples };

        await handler.HandleAsync(message, CancellationToken.None);

        await Assert.That(store.CallCount).IsEqualTo(1);
        await Assert.That(store.Received).IsNotNull();
        await Assert.That(store.Received!.Count).IsEqualTo(2);
        await Assert.That(store.Received[0].OrganizationId).IsEqualTo("org_a");
        await Assert.That(store.Received[1].OrganizationId).IsEqualTo("org_b");
    }

    private static MetricSample Sample(string organizationId) =>
        new(
            organizationId,
            null,
            "zeeq_tool_call_counter",
            1,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow
        );

    private sealed class RecordingMetricEventStore : IMetricEventStore
    {
        public IReadOnlyList<MetricSample>? Received { get; private set; }
        public int CallCount { get; private set; }

        public Task AppendAsync(
            IReadOnlyList<MetricSample> samples,
            CancellationToken cancellationToken
        )
        {
            Received = samples;
            CallCount++;

            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDeadLetterWriter : IDeadLetterWriter
    {
        public Task WriteAsync<TMessage>(
            TMessage message,
            IRequestContext? context,
            Exception? exception,
            CancellationToken cancellationToken = default
        )
            where TMessage : class, IRequest => Task.CompletedTask;
    }
}
