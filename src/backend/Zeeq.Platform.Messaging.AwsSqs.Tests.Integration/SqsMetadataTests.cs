namespace Zeeq.Platform.Messaging.AwsSqs.Tests.Integration;

/// <summary>Guards routing and validation before broker provisioning.</summary>
public sealed class SqsMetadataTests
{
    [Test]
    [Arguments("", "valid.route")]
    [Arguments("prefix-with-more-than-16", "valid.route")]
    [Arguments("valid", "invalid route")]
    [Arguments("valid", "invalid/route")]
    [Arguments("valid", "nonascii.é")]
    public async Task InvalidNames_AreRejected(string prefix, string route)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.FromResult(new AwsSqsResourceNameValidator().QueueName(prefix, route))
        );
    }

    [Test]
    public async Task QueueNames_RejectTruncation_At80CharacterBoundary()
    {
        var validator = new AwsSqsResourceNameValidator();
        await Assert
            .That(validator.QueueName("zeeq", new string('a', count: 75)).Length)
            .IsEqualTo(80);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.FromResult(validator.QueueName("zeeq", new string('a', count: 76)))
        );
    }

    [Test]
    public async Task Catalog_RejectsNormalizationCollisions()
    {
        var catalog = new MessagingCatalog(
            [
                Publisher(typeof(PrototypeSystemMessage), "a.b"),
                Publisher(typeof(PrototypeTenantMessage), "a_b"),
            ],
            []
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.FromResult(
                new AwsSqsMessagingTopology(catalog, new(), new() { QueuePrefix = "test" })
            )
        );
    }

    [Test]
    public async Task Catalog_RejectsIndependentConsumerFanout()
    {
        var catalog = new MessagingCatalog(
            [Publisher(typeof(PrototypeSystemMessage), "fanout")],
            [
                new(
                    typeof(PrototypeSystemHandler),
                    typeof(PrototypeSystemMessage),
                    "one",
                    1,
                    1,
                    30,
                    100
                ),
                new(
                    typeof(PrototypeSystemHandler),
                    typeof(PrototypeSystemMessage),
                    "two",
                    1,
                    1,
                    30,
                    100
                ),
            ]
        );
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Task.FromResult(
                new AwsSqsMessagingTopology(catalog, new(), new() { QueuePrefix = "test" })
            )
        );
    }

    [Test]
    public async Task Topology_Preserves900SecondVisibility_AndSeparatesLongPollFromEmptyDelay()
    {
        var catalog = new MessagingCatalog(
            [
                Publisher(typeof(PrototypeSystemMessage), "long.handler") with
                {
                    VisibleTimeoutSeconds = 900,
                },
            ],
            [
                new(
                    typeof(PrototypeSystemHandler),
                    typeof(PrototypeSystemMessage),
                    "handler.identity",
                    1,
                    1,
                    0,
                    500
                ),
            ]
        );
        var topology = new AwsSqsMessagingTopology(
            catalog,
            new(),
            new() { QueuePrefix = "test", LongPollSeconds = 20 }
        );
        var publication = topology.Publications.Single();
        var subscription = topology.Subscriptions.Single();
        await Assert.That(publication.ChannelName).IsEqualTo(subscription.ChannelName);
        await Assert.That(subscription.ChannelName.Value).IsEqualTo("test-long_handler_system");
        await Assert
            .That(subscription.QueueAttributes.LockTimeout)
            .IsEqualTo(TimeSpan.FromSeconds(900));
        await Assert
            .That(publication.QueueAttributes.LockTimeout)
            .IsEqualTo(subscription.QueueAttributes.LockTimeout);
        await Assert.That(subscription.TimeOut).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(subscription.EmptyChannelDelay).IsEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert
            .That(publication.DefaultHeaders![AwsSqsMessagingTopology.LogicalRouteHeader])
            .IsEqualTo("long.handler.system");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(21)]
    public async Task InvalidLongPoll_IsRejected(int seconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.FromResult(
                new AwsSqsMessagingTopology(
                    new([Publisher(typeof(PrototypeSystemMessage), "probe")], []),
                    new(),
                    new() { QueuePrefix = "test", LongPollSeconds = seconds }
                )
            )
        );
    }

    [Test]
    public async Task VisibilityBelowLongPoll_IsRejected_InsteadOfSilentlyReset()
    {
        var catalog = new MessagingCatalog(
            [Publisher(typeof(PrototypeSystemMessage), "probe") with { VisibleTimeoutSeconds = 5 }],
            []
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.FromResult(
                new AwsSqsMessagingTopology(
                    catalog,
                    new(),
                    new() { QueuePrefix = "test", LongPollSeconds = 20 }
                )
            )
        );
    }

    private static MessagingPublisher Publisher(Type messageType, string topic) =>
        new(
            messageType,
            topic,
            typeof(DefaultMessage),
            VisibleTimeoutSeconds: 0,
            BufferSize: 0,
            IsTenantMessage: false,
            IsSystemMessage: true
        );
}
