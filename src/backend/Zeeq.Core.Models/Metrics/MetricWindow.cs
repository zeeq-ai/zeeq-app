namespace Zeeq.Core.Models;

/// <summary>
/// Supported dashboard time windows for metric queries.
/// </summary>
/// <remarks>
/// The set is closed by design: endpoints accept only these windows so query
/// plans stay predictable and partition-pruned. Each window maps to a total
/// look-back span and a fixed bucket width via <see cref="MetricWindowExtensions.ToRange" />.
/// </remarks>
public enum MetricWindow
{
    /// <summary>15 minutes, 1-minute buckets.</summary>
    M15,

    /// <summary>30 minutes, 2-minute buckets.</summary>
    M30,

    /// <summary>1 hour, 5-minute buckets.</summary>
    H1,

    /// <summary>4 hours, 15-minute buckets.</summary>
    H4,

    /// <summary>12 hours, 30-minute buckets.</summary>
    H12,

    /// <summary>24 hours, 1-hour buckets.</summary>
    H24,

    /// <summary>7 days, 6-hour buckets.</summary>
    D7,

    /// <summary>14 days, 12-hour buckets.</summary>
    D14,

    /// <summary>30 days, 1-day buckets.</summary>
    D30,
}

/// <summary>
/// Total look-back span and fixed bucket width for a <see cref="MetricWindow" />.
/// </summary>
/// <param name="Span">How far back from now the window covers.</param>
/// <param name="Bucket">The fixed-width bucket used to aggregate the series.</param>
public readonly record struct MetricWindowRange(TimeSpan Span, TimeSpan Bucket);

/// <summary>
/// Mapping from a <see cref="MetricWindow" /> to its span and bucket width.
/// </summary>
public static class MetricWindowExtensions
{
    extension(MetricWindow window)
    {
        /// <summary>
        /// Resolves the total span and bucket width for this window.
        /// </summary>
        public MetricWindowRange ToRange() =>
            window switch
            {
                MetricWindow.M15 => new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1)),
                MetricWindow.M30 => new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2)),
                MetricWindow.H1 => new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5)),
                MetricWindow.H4 => new(TimeSpan.FromHours(4), TimeSpan.FromMinutes(15)),
                MetricWindow.H12 => new(TimeSpan.FromHours(12), TimeSpan.FromMinutes(30)),
                MetricWindow.H24 => new(TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
                MetricWindow.D7 => new(TimeSpan.FromDays(7), TimeSpan.FromHours(6)),
                MetricWindow.D14 => new(TimeSpan.FromDays(14), TimeSpan.FromHours(12)),
                MetricWindow.D30 => new(TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(window),
                    window,
                    "Unknown metric window."
                ),
            };
    }
}
