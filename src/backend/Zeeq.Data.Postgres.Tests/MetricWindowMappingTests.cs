using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Unit tests for the <see cref="MetricWindow" /> → span/bucket mapping. Pure
/// in-memory logic; asserts each window resolves to its documented span and
/// bucket width so the read-path bucketing stays aligned with the taxonomy.
/// </summary>
public sealed class MetricWindowMappingTests
{
    [Test]
    [Arguments(MetricWindow.M15, 15d, 1d)]
    [Arguments(MetricWindow.M30, 30d, 2d)]
    [Arguments(MetricWindow.H1, 60d, 5d)]
    [Arguments(MetricWindow.H4, 240d, 15d)]
    [Arguments(MetricWindow.H12, 720d, 30d)]
    [Arguments(MetricWindow.H24, 1440d, 60d)]
    [Arguments(MetricWindow.H72, 4320d, 120d)]
    [Arguments(MetricWindow.D7, 10080d, 360d)]
    [Arguments(MetricWindow.D14, 20160d, 720d)]
    [Arguments(MetricWindow.D30, 43200d, 1440d)]
    public async Task ToRange_MapsWindowToDocumentedSpanAndBucket(
        MetricWindow window,
        double expectedSpanMinutes,
        double expectedBucketMinutes
    )
    {
        var range = window.ToRange();

        await Assert.That(range.Span.TotalMinutes).IsEqualTo(expectedSpanMinutes);
        await Assert.That(range.Bucket.TotalMinutes).IsEqualTo(expectedBucketMinutes);
    }
}
