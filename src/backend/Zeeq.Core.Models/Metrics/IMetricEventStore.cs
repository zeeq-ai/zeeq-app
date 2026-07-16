namespace Zeeq.Core.Models;

/// <summary>
/// Write store for captured metric measurements.
/// </summary>
/// <remarks>
/// Domain interface so the messaging consumer depends on this abstraction rather
/// than <c>PostgresDbContext</c> directly (multi-provider data-layer rule). The
/// implementation batches a whole <see cref="MetricSample" /> list into one round
/// trip.
/// </remarks>
public interface IMetricEventStore
{
    /// <summary>
    /// Appends a batch of samples to durable metric storage in one round trip.
    /// </summary>
    /// <param name="samples">Samples to persist; a no-op when empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AppendAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken);
}
