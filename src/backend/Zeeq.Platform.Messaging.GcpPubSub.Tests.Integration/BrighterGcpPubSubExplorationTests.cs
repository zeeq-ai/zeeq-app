using Zeeq.Testing;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub.Tests.Integration;

/// <summary>
/// Explicit Pub/Sub emulator tests that prove Brighter's GCP primitives before
/// Zeeq builds an adapter or runtime DI around them.
/// </summary>
[Explicit]
[Category("PubSubEmulator")]
[ClassDataSource<GcpPubSubFixture>(Shared = SharedType.PerTestSession)]
public sealed class BrighterGcpPubSubExplorationTests(GcpPubSubFixture fixture)
{
    [Test]
    public async Task PubSubContainer_Starts_AndManagementClientsUseEndpoint()
    {
        var context = CreateContext("fixture");
        var publisher = await context.Connection.CreatePublisherServiceApiClientAsync();
        var topicName = context.TopicName("container-start");

        await publisher.CreateTopicAsync(topicName, context.CallSettings);

        var topic = await publisher.GetTopicAsync(topicName, context.CallSettings);

        await Assert.That(fixture.PubSubContainer.GetEmulatorEndpoint()).IsNotEmpty();
        await Assert.That(topic.TopicName).IsEqualTo(topicName);
    }

    [Test]
    public async Task ProducerRegistry_WithCreate_CreatesTopicOnEmulator()
    {
        var context = CreateContext("producer-create");
        var topic = context.TopicId("orders-created");

        using var registry = await new GcpPubSubProducerRegistryFactory(
            context.Connection,
            [context.Publication(topic, OnMissingChannel.Create)]
        ).CreateAsync(CancellationToken.None);

        var producer = registry.LookupAsyncBy(new RoutingKey(topic));

        await Assert.That(producer).IsAssignableTo<IAmAMessageProducerAsync>();
        await Assert.That(await context.TopicExistsAsync(topic)).IsTrue();
    }

    [Test]
    public async Task ChannelFactory_WithCreate_CreatesTopicAndSubscriptionOnEmulator()
    {
        var context = CreateContext("channel-create");
        var topic = context.TopicId("reviews-run");
        var subscription = context.SubscriptionId("reviews-worker");

        await using var channel = await new GcpPubSubChannelFactory(
            context.Connection
        ).CreateAsyncChannelAsync(
            context.Subscription(
                topic,
                subscription,
                OnMissingChannel.Create,
                SubscriptionMode.Pull
            ),
            CancellationToken.None
        );

        await Assert.That(channel.Name.Value).IsEqualTo(subscription);
        await Assert.That(await context.TopicExistsAsync(topic)).IsTrue();
        await Assert.That(await context.SubscriptionExistsAsync(subscription)).IsTrue();
    }

    [Test]
    public async Task ProducerRegistry_WithValidate_ThrowsWhenTopicMissing()
    {
        var context = CreateContext("producer-validate-missing");
        var topic = context.TopicId("missing-topic");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new GcpPubSubProducerRegistryFactory(
                context.Connection,
                [context.Publication(topic, OnMissingChannel.Validate)]
            ).CreateAsync(CancellationToken.None)
        );

        await Assert.That(exception!.Message).Contains("could not find topic");
        await Assert.That(await context.TopicExistsAsync(topic)).IsFalse();
    }

    [Test]
    public async Task ChannelFactory_WithValidate_ThrowsWhenSubscriptionMissing()
    {
        var context = CreateContext("subscription-validate-missing");
        var topic = context.TopicId("existing-topic");
        var subscription = context.SubscriptionId("missing-subscription");
        await context.CreateTopicAsync(topic);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new GcpPubSubChannelFactory(context.Connection).CreateAsyncChannelAsync(
                context.Subscription(
                    topic,
                    subscription,
                    OnMissingChannel.Validate,
                    SubscriptionMode.Pull
                ),
                CancellationToken.None
            )
        );

        await Assert.That(exception!.Message).Contains("No subscription found");
        await Assert.That(await context.SubscriptionExistsAsync(subscription)).IsFalse();
    }

    [Test]
    public async Task ChannelFactory_WithValidate_SucceedsAfterResourcesExist()
    {
        var context = CreateContext("validate-existing");
        var topic = context.TopicId("existing-topic");
        var subscription = context.SubscriptionId("existing-subscription");
        await context.CreateTopicAsync(topic);
        await context.CreateSubscriptionAsync(topic, subscription);

        await using var channel = await new GcpPubSubChannelFactory(
            context.Connection
        ).CreateAsyncChannelAsync(
            context.Subscription(
                topic,
                subscription,
                OnMissingChannel.Validate,
                SubscriptionMode.Pull
            ),
            CancellationToken.None
        );

        await Assert.That(channel.Name.Value).IsEqualTo(subscription);
    }

    [Test]
    public async Task Assume_DoesNotCreateBrokerResources()
    {
        var context = CreateContext("assume-no-create");
        var topic = context.TopicId("assumed-topic");
        var subscription = context.SubscriptionId("assumed-subscription");

        using var registry = await new GcpPubSubProducerRegistryFactory(
            context.Connection,
            [context.Publication(topic, OnMissingChannel.Assume)]
        ).CreateAsync(CancellationToken.None);

        await using var channel = await new GcpPubSubChannelFactory(
            context.Connection
        ).CreateAsyncChannelAsync(
            context.Subscription(
                topic,
                subscription,
                OnMissingChannel.Assume,
                SubscriptionMode.Pull
            ),
            CancellationToken.None
        );

        await Assert.That(registry.LookupAsyncBy(new RoutingKey(topic))).IsNotNull();
        await Assert.That(channel.Name.Value).IsEqualTo(subscription);
        await Assert.That(await context.TopicExistsAsync(topic)).IsFalse();
        await Assert.That(await context.SubscriptionExistsAsync(subscription)).IsFalse();
    }

    [Test]
    public async Task PullMode_PublishAndConsume_RoundTripsMessage()
    {
        var context = CreateContext("pull-roundtrip");
        var topic = context.TopicId("diagnostics-topic");
        var subscription = context.SubscriptionId("diagnostics-worker");
        var payload = $"payload-{Guid.NewGuid():N}";

        await using var channel = await new GcpPubSubChannelFactory(
            context.Connection
        ).CreateAsyncChannelAsync(
            context.Subscription(
                topic,
                subscription,
                OnMissingChannel.Create,
                SubscriptionMode.Pull
            ),
            CancellationToken.None
        );

        using var registry = await new GcpPubSubProducerRegistryFactory(
            context.Connection,
            [context.Publication(topic, OnMissingChannel.Create)]
        ).CreateAsync(CancellationToken.None);

        await registry
            .LookupAsyncBy(new RoutingKey(topic))
            .SendAsync(context.Message(topic, payload), CancellationToken.None);

        var received = await context.ReceiveNonEmptyAsync(channel);
        await channel.AcknowledgeAsync(received, CancellationToken.None);

        await Assert.That(received.Header.Topic.Value).IsEqualTo(topic);
        await Assert.That(received.Body.Value).IsEqualTo(payload);
    }

    [Test]
    public async Task StreamMode_ChannelCreation_UsesExplicitStreamConfiguration()
    {
        var context = CreateContext("stream-create");
        var topic = context.TopicId("stream-topic");
        var subscription = context.SubscriptionId("stream-worker");

        await using var channel = await new GcpPubSubChannelFactory(
            context.Connection
        ).CreateAsyncChannelAsync(
            context.Subscription(
                topic,
                subscription,
                OnMissingChannel.Create,
                SubscriptionMode.Stream
            ),
            CancellationToken.None
        );

        await Assert.That(channel.Name.Value).IsEqualTo(subscription);
        await Assert.That(await context.TopicExistsAsync(topic)).IsTrue();
        await Assert.That(await context.SubscriptionExistsAsync(subscription)).IsTrue();
    }

    private static string CreateId(string prefix)
    {
        var safePrefix = prefix.Replace('.', '-');
        return $"zeeq-{safePrefix}-{Guid.NewGuid():N}"[..48];
    }

    private PubSubExplorationContext CreateContext(string scenario) =>
        new(scenario, fixture.PubSubContainer.GetEmulatorEndpoint());

    private sealed class PubSubExplorationContext
    {
        public PubSubExplorationContext(string scenario, string emulatorEndpoint)
        {
            ProjectId = CreateId($"project-{scenario}");
            EmulatorEndpoint = emulatorEndpoint;
            Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", emulatorEndpoint);

            Connection = new GcpMessagingGatewayConnection
            {
                ProjectId = ProjectId,
                TopicManagerConfiguration = ConfigureEmulator,
                PublisherConfiguration = ConfigureEmulator,
                SubscriptionManagerConfiguration = ConfigureEmulator,
                StreamConfiguration = ConfigureEmulator,
            };
        }

        public string ProjectId { get; }

        public string EmulatorEndpoint { get; }

        public GcpMessagingGatewayConnection Connection { get; }

        public CallSettings CallSettings =>
            CallSettings.FromCancellationToken(CancellationToken.None);

        public Google.Cloud.PubSub.V1.TopicName TopicName(string topic) =>
            Google.Cloud.PubSub.V1.TopicName.FromProjectTopic(ProjectId, topic);

        public Google.Cloud.PubSub.V1.SubscriptionName SubscriptionName(string subscription) =>
            Google.Cloud.PubSub.V1.SubscriptionName.FromProjectSubscription(
                ProjectId,
                subscription
            );

        public string TopicId(string name) => CreateId(name);

        public string SubscriptionId(string name) => CreateId(name);

        public GcpPublication Publication(string topic, OnMissingChannel makeChannels) =>
            new()
            {
                Topic = new RoutingKey(topic),
                RequestType = typeof(ExplorationMessage),
                MakeChannels = makeChannels,
            };

        public GcpPubSubSubscription Subscription(
            string topic,
            string subscription,
            OnMissingChannel makeChannels,
            SubscriptionMode subscriptionMode
        ) =>
            new(
                subscriptionName: new Paramore.Brighter.SubscriptionName(subscription),
                channelName: new Paramore.Brighter.ChannelName(subscription),
                routingKey: new RoutingKey(topic),
                requestType: typeof(ExplorationMessage),
                bufferSize: 1,
                noOfPerformers: 1,
                timeOut: TimeSpan.FromMilliseconds(100),
                messagePumpType: MessagePumpType.Proactor,
                makeChannels: makeChannels,
                emptyChannelDelay: TimeSpan.FromMilliseconds(100),
                projectId: ProjectId,
                ackDeadlineSeconds: 10,
                subscriptionMode: subscriptionMode
            );

        public Message Message(string topic, string payload) =>
            new(
                new MessageHeader(
                    new Id($"message-{Guid.NewGuid():N}"),
                    new RoutingKey(topic),
                    MessageType.MT_EVENT
                ),
                new MessageBody(payload)
            );

        public async Task CreateTopicAsync(string topic)
        {
            var publisher = await Connection.CreatePublisherServiceApiClientAsync();
            await publisher.CreateTopicAsync(TopicName(topic), CallSettings);
        }

        public async Task CreateSubscriptionAsync(string topic, string subscription)
        {
            var subscriber = await Connection.CreateSubscriberServiceApiClientAsync();
            await subscriber.CreateSubscriptionAsync(
                SubscriptionName(subscription),
                TopicName(topic),
                pushConfig: null,
                ackDeadlineSeconds: 10,
                CallSettings
            );
        }

        public async Task<bool> TopicExistsAsync(string topic)
        {
            var publisher = await Connection.CreatePublisherServiceApiClientAsync();

            try
            {
                await publisher.GetTopicAsync(TopicName(topic), CallSettings);
                return true;
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task<bool> SubscriptionExistsAsync(string subscription)
        {
            var subscriber = await Connection.CreateSubscriberServiceApiClientAsync();

            try
            {
                await subscriber.GetSubscriptionAsync(SubscriptionName(subscription), CallSettings);
                return true;
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task<Message> ReceiveNonEmptyAsync(IAmAChannelAsync channel)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var message = await channel.ReceiveAsync(
                    TimeSpan.FromMilliseconds(200),
                    CancellationToken.None
                );

                if (!message.IsEmpty)
                {
                    return message;
                }

                await Task.Delay(100, CancellationToken.None);
            }

            throw new TimeoutException("Timed out waiting for a non-empty Pub/Sub message.");
        }

        private void ConfigureEmulator(PublisherServiceApiClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }

        private void ConfigureEmulator(PublisherClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }

        private void ConfigureEmulator(SubscriberServiceApiClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }

        private void ConfigureEmulator(SubscriberClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }
    }

    private sealed class ExplorationMessage : Event
    {
        public ExplorationMessage()
            : base(Id.Random()) { }
    }
}
