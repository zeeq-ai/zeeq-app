namespace Zeeq.Platform.Messaging.AwsSqs.Tests.Integration;

/// <summary>Verifies the real mapper, routing, retry/fallback and acknowledgement pipeline.</summary>
[Category("SqsEmulator")]
[ClassDataSource<FlociFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("SqsPipeline")]
public sealed class SqsPipelineTests(FlociFixture fixture)
{
    [Test]
    public async Task ZeeqPublisher_ReachesHandlers_AndAcknowledgesSuccessfulFallback()
    {
        var result = await SqsPipelinePrototype.RunAsync(fixture.Endpoint);
        await Assert.That(result.MessagesHandled).IsEqualTo(5);
        await Assert.That(result.PoisonAttempts).IsEqualTo(4);
        await Assert.That(result.QueuesCreated).IsEqualTo(6);
        await Assert.That(result.Routes.Count).IsEqualTo(4);
        await Assert.That(result.ShutdownSeconds).IsLessThan(10);
    }
}
