namespace Zeeq.Core.Models;

/// <summary>Resolved half-open UTC bounds and bucket width shared by metrics read queries.</summary>
/// <param name="StartUtc">Inclusive start and origin for all aggregate buckets.</param>
/// <param name="EndExclusiveUtc">Exclusive end, capped at the request's captured current time.</param>
/// <param name="Bucket">Fixed aggregation width, independent of a partial current day.</param>
public readonly record struct MetricQueryRange(
    DateTimeOffset StartUtc,
    DateTimeOffset EndExclusiveUtc,
    TimeSpan Bucket
);
