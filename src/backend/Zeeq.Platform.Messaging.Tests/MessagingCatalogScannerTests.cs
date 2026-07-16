using Zeeq.Core.Models;
using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.Tests;

/// <summary>
/// Unit tests for messaging catalog discovery.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Messaging.Tests --output detailed --disable-logo
/// </summary>
public sealed class MessagingCatalogScannerTests
{
    [Test]
    public async Task Scan_WithPublisherAndConsumerAttributes_DiscoversCatalogMetadata()
    {
        var scanner = new MessagingCatalogScanner();

        var catalog = scanner.Scan(typeof(ScannerTenantMessage).Assembly);
        var publisher = catalog.FindPublisher(typeof(ScannerTenantMessage));
        var consumer = catalog.Consumers.Single(c =>
            c.HandlerType == typeof(ScannerTenantMessageHandler)
        );

        await Assert.That(publisher).IsNotNull();
        await Assert.That(publisher!.Topic).IsEqualTo("scanner.tenant");
        await Assert.That(publisher.PriorityType).IsEqualTo(typeof(LowPriorityMessage));
        await Assert.That(publisher.IsTenantMessage).IsTrue();
        await Assert.That(publisher.IsImmediateMessage).IsFalse();
        await Assert.That(consumer.ChannelName).IsEqualTo("scanner.tenant.worker");
        await Assert.That(consumer.NoOfPerformers).IsEqualTo(2);
        await Assert.That(consumer.BufferSize).IsEqualTo(3);
        await Assert.That(consumer.VisibleTimeoutSeconds).IsEqualTo(45);
        await Assert.That(consumer.PollIntervalMilliseconds).IsEqualTo(250);
    }

    [Test]
    public async Task Scan_WithImmediatePublisher_DiscoversImmediateMetadata()
    {
        var scanner = new MessagingCatalogScanner();

        var catalog = scanner.Scan(typeof(ScannerImmediateMessage).Assembly);
        var publisher = catalog.FindPublisher(typeof(ScannerImmediateMessage));

        await Assert.That(publisher).IsNotNull();
        await Assert.That(publisher!.Topic).IsEqualTo("scanner.immediate");
        await Assert.That(publisher.PriorityType).IsEqualTo(typeof(ImmediateMessage));
        await Assert.That(publisher.IsTenantMessage).IsTrue();
        await Assert.That(publisher.IsImmediateMessage).IsTrue();
    }

    [Test]
    public async Task GetPublisherTopic_WithKnownMessage_ReturnsTopic()
    {
        var catalog = new MessagingCatalog(
            [
                new MessagingPublisher(
                    typeof(ScannerSystemMessage),
                    "scanner.system",
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 10,
                    IsTenantMessage: false,
                    IsSystemMessage: true
                ),
            ],
            []
        );

        var topic = catalog.GetPublisherTopic(typeof(ScannerSystemMessage));

        await Assert.That(topic).IsEqualTo("scanner.system");
    }

    [ConfigurePublisher<LowPriorityMessage>("scanner.tenant")]
    private sealed class ScannerTenantMessage(string organizationId, string? teamId)
        : Event(Id.Random()),
            ITenantMessage
    {
        public string OrganizationId { get; } = organizationId;

        public string? TeamId { get; } = teamId;
    }

    [ConfigureConsumer<ScannerTenantMessage>(
        channelName: "scanner.tenant.worker",
        noOfPerformers: 2,
        bufferSize: 3,
        visibleTimeoutSeconds: 45,
        pollIntervalMilliseconds: 250
    )]
    private sealed class ScannerTenantMessageHandler(IDeadLetterWriter deadLetterWriter)
        : ZeeqMessageHandler<ScannerTenantMessage>(deadLetterWriter)
    {
        protected override Task<ScannerTenantMessage> HandleMessageAsync(
            ScannerTenantMessage message,
            CancellationToken cancellationToken
        ) => Task.FromResult(message);
    }

    private sealed class ScannerSystemMessage : Event, ISystemMessage
    {
        public ScannerSystemMessage()
            : base(Id.Random()) { }
    }

    [ConfigurePublisher<ImmediateMessage>("scanner.immediate")]
    private sealed class ScannerImmediateMessage(string organizationId, string? teamId)
        : Event(Id.Random()),
            ITenantMessage
    {
        public string OrganizationId { get; } = organizationId;

        public string? TeamId { get; } = teamId;
    }
}
