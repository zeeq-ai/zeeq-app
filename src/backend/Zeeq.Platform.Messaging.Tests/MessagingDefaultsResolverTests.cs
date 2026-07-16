namespace Zeeq.Platform.Messaging.Tests;

public sealed class MessagingDefaultsResolverTests
{
    [Test]
    public async Task ResolveDefaults_WithOutOfRangeConfiguredDefaults_ClampsValues()
    {
        var options = new ZeeqMessagingOptions
        {
            Defaults = new MessagingDefaultsOptions
            {
                BufferSize = -5,
                NoOfPerformers = 100,
                VisibleTimeoutSeconds = 7200,
                PollIntervalMilliseconds = 1,
            },
        };
        var publisher = Publisher();

        var defaults = options.ResolveDefaults(publisher);

        await Assert.That(defaults.BufferSize).IsEqualTo(1);
        await Assert.That(defaults.NoOfPerformers).IsEqualTo(32);
        await Assert.That(defaults.VisibleTimeoutSeconds).IsEqualTo(3600);
        await Assert.That(defaults.PollIntervalMilliseconds).IsEqualTo(100);
    }

    [Test]
    public async Task ResolveDefaults_WithOutOfRangeAttributeOverrides_ClampsValues()
    {
        var options = new ZeeqMessagingOptions();
        var publisher = Publisher(bufferSize: 100, visibleTimeoutSeconds: -1);
        var consumer = new MessagingConsumer(
            typeof(ResolverHandler),
            typeof(ResolverMessage),
            "resolver.worker",
            NoOfPerformers: -1,
            BufferSize: 100,
            VisibleTimeoutSeconds: 7200,
            PollIntervalMilliseconds: 1
        );

        var defaults = options.ResolveDefaults(publisher, consumer);

        await Assert.That(defaults.BufferSize).IsEqualTo(10);
        await Assert.That(defaults.NoOfPerformers).IsEqualTo(1);
        await Assert.That(defaults.VisibleTimeoutSeconds).IsEqualTo(3600);
        await Assert.That(defaults.PollIntervalMilliseconds).IsEqualTo(100);
    }

    [Test]
    public async Task ResolveDefaults_WithImmediateMessage_UsesBrighterSafeBufferAndFastPolling()
    {
        var options = new ZeeqMessagingOptions();
        var publisher = Publisher(priorityType: typeof(ImmediateMessage));

        var defaults = options.ResolveDefaults(publisher);

        await Assert.That(defaults.BufferSize).IsEqualTo(10);
        await Assert.That(defaults.NoOfPerformers).IsEqualTo(16);
        await Assert.That(defaults.VisibleTimeoutSeconds).IsEqualTo(60);
        await Assert.That(defaults.PollIntervalMilliseconds).IsEqualTo(50);
    }

    [Test]
    public async Task ResolveDefaults_WithImmediateAttributeOverride_ClampsBufferToBrighterMaximum()
    {
        var options = new ZeeqMessagingOptions();
        var publisher = Publisher(priorityType: typeof(ImmediateMessage), bufferSize: 500);
        var consumer = new MessagingConsumer(
            typeof(ResolverHandler),
            typeof(ResolverMessage),
            "resolver.worker",
            NoOfPerformers: 0,
            BufferSize: 500,
            VisibleTimeoutSeconds: 0,
            PollIntervalMilliseconds: 1
        );

        var defaults = options.ResolveDefaults(publisher, consumer);

        await Assert.That(defaults.BufferSize).IsEqualTo(10);
        await Assert.That(defaults.PollIntervalMilliseconds).IsEqualTo(50);
    }

    private static MessagingPublisher Publisher(
        int bufferSize = 0,
        int visibleTimeoutSeconds = 0,
        Type? priorityType = null
    ) =>
        new(
            typeof(ResolverMessage),
            "resolver.message",
            priorityType ?? typeof(DefaultMessage),
            visibleTimeoutSeconds,
            bufferSize,
            IsTenantMessage: true,
            IsSystemMessage: false
        );

    private sealed class ResolverMessage;

    private sealed class ResolverHandler;
}
