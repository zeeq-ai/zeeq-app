using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Open.ChannelExtensions;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Captures measurements from the shared Zeeq meter and batches them onto the
/// durable metrics write path.
/// </summary>
/// <remarks>
/// <para>
/// One hosted service owns both the <see cref="MeterListener" /> and the flush
/// loop. It runs in every producer process (web emits MCP metrics, worker emits
/// review metrics). The listener enables measurement events only for
/// <see cref="ZeeqTelemetry.Metrics" />; every other meter is untouched and
/// continues flowing to OTEL exactly as before.
/// </para>
/// <para>
/// <b>Capture rule</b> (the only filter in the pipeline): a measurement is
/// persisted iff it comes from the Zeeq meter <b>and</b> carries an
/// <c>organization_id</c> tag. This makes the pipeline self-selecting and
/// automatically excludes its own internal diagnostics (which carry no
/// <c>organization_id</c>) — the feedback-loop guard.
/// </para>
/// <para>
/// <b>Loss-tolerant by contract</b> (NFR-4): the bounded channel drops the newest
/// sample when full, and publish failures are swallowed-and-logged rather than
/// rethrown, so metrics can never take a producer process down.
/// </para>
/// </remarks>
public sealed partial class MetricsIngestionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetricsIngestionHostedService> logger
) : BackgroundService
{
    private const int ChannelCapacity = 10_000;
    private const int FlushBatchSize = 1_000;
    private static readonly TimeSpan FlushLinger = TimeSpan.FromSeconds(2);

    // DropWrite: a full channel discards the *new* sample (NFR-4). SingleReader:
    // only the flush loop reads.
    private readonly Channel<MetricSample> _channel = Channel.CreateBounded<MetricSample>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }
    );

    private long _dropped;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            // Only the shared Zeeq meter; every other meter is left to OTEL.
            if (ReferenceEquals(instrument.Meter, ZeeqTelemetry.Metrics))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>(OnMeasurement);
        listener.SetMeasurementEventCallback<long>(OnMeasurement);
        listener.SetMeasurementEventCallback<double>(OnMeasurement);
        listener.Start();

        // Count-or-linger: flush at FlushBatchSize samples or after FlushLinger,
        // whichever comes first. Batch(...).WithTimeout(...) is the Open.ChannelExtensions
        // count-or-linger reader; the built-in ReadAllAsync drains the batches.
        try
        {
            await foreach (
                var batch in _channel
                    .Reader.Batch(FlushBatchSize)
                    .WithTimeout(FlushLinger)
                    .ReadAllAsync(stoppingToken)
            )
            {
                await PublishBatchAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // NOTE: Graceful shutdown. Samples still buffered in the channel — or a partial
            // batch shorter than the linger window — are intentionally dropped rather than
            // drained on stop: metrics are loss-tolerant (NFR-4), so trading a tiny tail of
            // samples for a clean, prompt shutdown is the right call.
        }
    }

    /// <summary>
    /// Converts one measurement to a <see cref="MetricSample" /> and enqueues it.
    /// </summary>
    /// <remarks>
    /// On the hot path of every counter increment: no locks, no I/O, only the
    /// sample + optional residual-tags dictionary allocation and a non-blocking
    /// <c>TryWrite</c>. A measurement without an <c>organization_id</c> tag is
    /// dropped here (the capture rule).
    /// </remarks>
    private void OnMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state
    )
        where T : struct
    {
        // Pattern-match the known instrument value types (Counter<int>/<long>, Histogram<double>)
        // to avoid boxing on the capture hot path; each generic instantiation is JIT-specialized,
        // so the matching arm is a direct conversion. Only int/long/double ever reach here — those
        // are the only types registered via SetMeasurementEventCallback<T> in ExecuteAsync above —
        // so the default arm below is unreachable today. It exists to fail safely (drop + log)
        // rather than via a runtime Convert.ToDouble throw on the hot path, in case a future
        // instrument type is registered without updating this switch.
        double value;
        switch (measurement)
        {
            case int intMeasurement:
                value = intMeasurement;
                break;
            case long longMeasurement:
                value = longMeasurement;
                break;
            case double doubleMeasurement:
                value = doubleMeasurement;
                break;
            default:
                LogUnsupportedMeasurementType(logger, instrument.Name, typeof(T).Name);
                return;
        }

        var sample = MetricMeasurementMapper.TryCreate(
            instrument.Name,
            value,
            tags,
            DateTimeOffset.UtcNow
        );

        // Capture rule (no organization_id) → not persisted; still flows to OTEL.
        if (sample is null)
        {
            return;
        }

        if (!_channel.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task PublishBatchAsync(
        IReadOnlyList<MetricSample> batch,
        CancellationToken cancellationToken
    )
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            LogSamplesDropped(logger, dropped);
        }

        await using var scope = scopeFactory.CreateAsyncScope();

        // Publish failures are swallowed-and-logged, never rethrown: metrics are
        // acceptable to lose (NFR-4) and must not take the host down.
        try
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IZeeqMessagePublisher>();
            await publisher.PublishAsync(
                new MetricBatchMessage { Samples = batch },
                cancellationToken
            );
            LogBatchPublished(logger, batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPublishFailed(ex, logger, batch.Count);
        }
    }

    [LoggerMessage(
        EventId = 5200,
        Level = LogLevel.Debug,
        Message = "Published metric batch. SampleCount={SampleCount}"
    )]
    private static partial void LogBatchPublished(ILogger logger, int sampleCount);

    [LoggerMessage(
        EventId = 5201,
        Level = LogLevel.Warning,
        Message = "Failed to publish metric batch; dropping it. SampleCount={SampleCount}"
    )]
    private static partial void LogPublishFailed(
        Exception exception,
        ILogger logger,
        int sampleCount
    );

    [LoggerMessage(
        EventId = 5202,
        Level = LogLevel.Warning,
        Message = "Dropped metric samples since last flush (capture channel full). DroppedCount={DroppedCount}"
    )]
    private static partial void LogSamplesDropped(ILogger logger, long droppedCount);

    [LoggerMessage(
        EventId = 5203,
        Level = LogLevel.Warning,
        Message = "Dropped a measurement with an unsupported value type. Instrument={InstrumentName}, ValueType={ValueType}"
    )]
    private static partial void LogUnsupportedMeasurementType(
        ILogger logger,
        string instrumentName,
        string valueType
    );
}
