namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests process-local code-review runtime percentile tracking.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewRuntimeDigestTests/*"
/// </summary>
public sealed class CodeReviewRuntimeDigestTests
{
    [Test]
    public async Task GetSnapshot_WithNoRecordedRuntimes_ReturnsNoData()
    {
        var digest = new CodeReviewRuntimeDigest();

        var snapshot = digest.GetSnapshot();

        await Assert.That(snapshot.HasData).IsFalse();
        await Assert.That(snapshot.Percentile50).IsNull();
        await Assert.That(snapshot.Percentile95).IsNull();
    }

    [Test]
    public async Task RecordAsync_WithRuntimeSamples_UpdatesPercentileSnapshot()
    {
        var digest = new CodeReviewRuntimeDigest();

        await digest.RecordAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await digest.RecordAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        await digest.RecordAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        var snapshot = digest.GetSnapshot();

        await Assert.That(snapshot.HasData).IsTrue();
        await Assert.That(snapshot.SampleCount).IsEqualTo(3);
        await Assert.That(snapshot.Percentile50).IsNotNull();
        await Assert.That(snapshot.Percentile95).IsNotNull();
        await Assert
            .That(snapshot.Percentile50!.Value)
            .IsBetween(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        await Assert
            .That(snapshot.Percentile95!.Value)
            .IsBetween(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }
}
