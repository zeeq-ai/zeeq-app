using TDigestNet;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Tracks process-local code-review runtime percentiles with a singleton T-Digest.
/// </summary>
/// <remarks>
/// T-Digest mutations are not thread-safe, so writes take a short lock while
/// adding the sample and publishing a new immutable snapshot. Reads deliberately
/// use a volatile snapshot instead of taking that lock; this keeps GitHub
/// comment rendering from contending with review-completion writes, at the cost
/// of reading the last published snapshot rather than the mutable digest itself.
///
/// The digest intentionally lives only for the application process lifetime; it
/// is used for live PR-comment context rather than durable analytics.
/// </remarks>
public sealed class CodeReviewRuntimeDigest : ICodeReviewRuntimeStatistics
{
    private readonly Lock _digestLock = new();
    private readonly TDigest _digest = new();
    private CodeReviewRuntimePercentilesSnapshot _snapshot =
        CodeReviewRuntimePercentilesSnapshot.NoData;

    /// <inheritdoc />
    public ValueTask RecordAsync(TimeSpan runtime, CancellationToken cancellationToken)
    {
        if (runtime < TimeSpan.Zero || !double.IsFinite(runtime.TotalMilliseconds))
        {
            return ValueTask.CompletedTask;
        }

        lock (_digestLock)
        {
            _digest.Add(runtime.TotalMilliseconds);

            Volatile.Write(
                ref _snapshot,
                new(
                    SampleCount: (long)_digest.Count,
                    Percentile50: TimeSpan.FromMilliseconds(_digest.Quantile(0.50)),
                    Percentile95: TimeSpan.FromMilliseconds(_digest.Quantile(0.95))
                )
            );
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public CodeReviewRuntimePercentilesSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);
}

/// <summary>
/// Provides code-review runtime observations and percentile snapshots.
/// </summary>
public interface ICodeReviewRuntimeStatistics
{
    /// <summary>
    /// Records one completed code-review runtime observation.
    /// </summary>
    ValueTask RecordAsync(TimeSpan runtime, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the latest process-local percentile snapshot.
    /// </summary>
    CodeReviewRuntimePercentilesSnapshot GetSnapshot();
}

/// <summary>
/// Process-local percentile snapshot for code-review runtimes.
/// </summary>
public sealed record CodeReviewRuntimePercentilesSnapshot(
    long SampleCount,
    TimeSpan? Percentile50,
    TimeSpan? Percentile95
)
{
    /// <summary>
    /// Snapshot used before any code-review runtime observations are available.
    /// </summary>
    public static CodeReviewRuntimePercentilesSnapshot NoData { get; } =
        new(SampleCount: 0, Percentile50: null, Percentile95: null);

    /// <summary>
    /// Indicates whether this snapshot has at least one runtime observation.
    /// </summary>
    public bool HasData => SampleCount > 0 && Percentile50.HasValue && Percentile95.HasValue;
}
