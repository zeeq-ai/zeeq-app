using System.Reflection;
using Zeeq.Testing;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub.Tests.Integration;

/// <summary>
/// Explicit Pub/Sub emulator tests that simulate the future runtime setup shape
/// using local test-only message and handler classes.
/// </summary>
[Explicit]
[Category("PubSubEmulator")]
[ClassDataSource<GcpPubSubFixture>(Shared = SharedType.PerTestSession)]
public sealed class LocalSetupSimulationTests(GcpPubSubFixture fixture)
{
    [Test]
    public async Task ProducerOnlySetup_ScansLocalCatalog_AndCreatesTopicsOnly()
    {
        var context = CreateContext("producer-setup");

        using var setup = await LocalGcpPubSubSetup.CreateProducerSetupAsync(
            context,
            typeof(LocalSetupSimulationTests).Assembly
        );

        var publication = setup.Publications.Single();
        var topic = publication.Topic!;

        await Assert.That(setup.Catalog.Publishers).HasSingleItem();
        await Assert.That(setup.Catalog.Consumers).HasSingleItem();
        await Assert.That(publication.RequestType).IsEqualTo(typeof(LocalSetupMessage));
        await Assert.That(topic.Value).IsEqualTo(context.TopicId("local.setup.proof"));
        await Assert.That(await context.TopicExistsAsync(topic.Value)).IsTrue();
        await Assert.That(await context.SubscriptionCountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task ConsumerOnlySetup_ScansLocalCatalog_AndCreatesSubscriptions()
    {
        var context = CreateContext("consumer-setup");

        await using var setup = await LocalGcpPubSubSetup.CreateConsumerSetupAsync(
            context,
            typeof(LocalSetupSimulationTests).Assembly
        );

        var subscription = setup.Subscriptions.Single();

        await Assert.That(setup.Catalog.Publishers).HasSingleItem();
        await Assert.That(setup.Catalog.Consumers).HasSingleItem();
        await Assert.That(subscription.RequestType).IsEqualTo(typeof(LocalSetupMessage));
        await Assert
            .That(subscription.RoutingKey.Value)
            .IsEqualTo(context.TopicId("local.setup.proof"));
        await Assert.That(subscription.SubscriptionMode).IsEqualTo(SubscriptionMode.Pull);
        await Assert.That(await context.TopicExistsAsync(subscription.RoutingKey.Value)).IsTrue();
        await Assert.That(await context.SubscriptionExistsAsync(subscription.Name.Value)).IsTrue();
    }

    [Test]
    public async Task SetupGeneratedProducerAndConsumer_CanRoundTripMessage()
    {
        var context = CreateContext("setup-roundtrip");
        var payload = $"setup-payload-{Guid.NewGuid():N}";

        await using var consumerSetup = await LocalGcpPubSubSetup.CreateConsumerSetupAsync(
            context,
            typeof(LocalSetupSimulationTests).Assembly
        );
        using var producerSetup = await LocalGcpPubSubSetup.CreateProducerSetupAsync(
            context,
            typeof(LocalSetupSimulationTests).Assembly
        );

        var publication = producerSetup.Publications.Single();
        var topic = publication.Topic!;
        var channel = consumerSetup.Channels.Single();

        await producerSetup
            .Registry.LookupAsyncBy(topic)
            .SendAsync(context.Message(topic.Value, payload), CancellationToken.None);

        var received = await context.ReceiveNonEmptyAsync(channel);
        await channel.AcknowledgeAsync(received, CancellationToken.None);

        await Assert.That(received.Header.Topic.Value).IsEqualTo(topic.Value);
        await Assert.That(received.Body.Value).IsEqualTo(payload);
    }

    private LocalPubSubSetupContext CreateContext(string scenario) =>
        new(scenario, fixture.PubSubContainer.GetEmulatorEndpoint());

    private sealed class LocalGcpPubSubSetup
    {
        public static async Task<LocalProducerSetup> CreateProducerSetupAsync(
            LocalPubSubSetupContext context,
            Assembly assembly
        )
        {
            var catalog = BuildCatalog(assembly);
            var publications = CreatePublications(catalog, context);
            var registry = await new GcpPubSubProducerRegistryFactory(
                context.Connection,
                publications
            ).CreateAsync(CancellationToken.None);

            return new(catalog, publications, registry);
        }

        public static async Task<LocalConsumerSetup> CreateConsumerSetupAsync(
            LocalPubSubSetupContext context,
            Assembly assembly
        )
        {
            var catalog = BuildCatalog(assembly);
            var subscriptions = CreateSubscriptions(catalog, context);
            var channelFactory = new GcpPubSubChannelFactory(context.Connection);
            var channels = new List<IAmAChannelAsync>(subscriptions.Count);

            foreach (var subscription in subscriptions)
            {
                channels.Add(
                    await channelFactory.CreateAsyncChannelAsync(
                        subscription,
                        CancellationToken.None
                    )
                );
            }

            return new(catalog, subscriptions, channels);
        }

        private static MessagingCatalog BuildCatalog(Assembly assembly)
        {
            var catalog = new MessagingCatalogScanner().Scan(assembly);

            new MessagingConventionValidator().ValidateAndThrow(catalog);

            return catalog;
        }

        private static IReadOnlyList<GcpPublication> CreatePublications(
            MessagingCatalog catalog,
            LocalPubSubSetupContext context
        ) =>
            [
                .. catalog.Publishers.Select(publisher => new GcpPublication
                {
                    Topic = new RoutingKey(context.TopicId(publisher.Topic)),
                    RequestType = publisher.MessageType,
                    MakeChannels = OnMissingChannel.Create,
                }),
            ];

        private static IReadOnlyList<GcpPubSubSubscription> CreateSubscriptions(
            MessagingCatalog catalog,
            LocalPubSubSetupContext context
        ) =>
            [
                .. catalog.Consumers.SelectMany(consumer =>
                    CreateSubscriptions(catalog, consumer, context)
                ),
            ];

        private static IEnumerable<GcpPubSubSubscription> CreateSubscriptions(
            MessagingCatalog catalog,
            MessagingConsumer consumer,
            LocalPubSubSetupContext context
        )
        {
            var publisher = catalog.FindPublisher(consumer.MessageType);
            if (publisher is null)
            {
                yield break;
            }

            var defaults = context.MessagingOptions.ResolveDefaults(publisher, consumer);
            var topic = context.TopicId(publisher.Topic);
            var subscription = context.SubscriptionId(consumer.ChannelName, publisher.Topic);

            yield return new GcpPubSubSubscription(
                subscriptionName: new Paramore.Brighter.SubscriptionName(subscription),
                channelName: new Paramore.Brighter.ChannelName(subscription),
                routingKey: new RoutingKey(topic),
                requestType: consumer.MessageType,
                bufferSize: defaults.BufferSize,
                noOfPerformers: defaults.NoOfPerformers,
                timeOut: TimeSpan.FromMilliseconds(defaults.PollIntervalMilliseconds),
                messagePumpType: MessagePumpType.Proactor,
                makeChannels: OnMissingChannel.Create,
                emptyChannelDelay: TimeSpan.FromMilliseconds(defaults.PollIntervalMilliseconds),
                projectId: context.ProjectId,
                ackDeadlineSeconds: Math.Clamp(defaults.VisibleTimeoutSeconds, 10, 600),
                subscriptionMode: SubscriptionMode.Pull
            );
        }
    }

    private sealed record LocalProducerSetup(
        MessagingCatalog Catalog,
        IReadOnlyList<GcpPublication> Publications,
        IAmAProducerRegistry Registry
    ) : IDisposable
    {
        public void Dispose()
        {
            Registry.CloseAll();

            if (Registry is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed record LocalConsumerSetup(
        MessagingCatalog Catalog,
        IReadOnlyList<GcpPubSubSubscription> Subscriptions,
        IReadOnlyList<IAmAChannelAsync> Channels
    ) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var channel in Channels)
            {
                await channel.DisposeAsync();
            }
        }
    }

    private sealed class LocalPubSubSetupContext
    {
        public LocalPubSubSetupContext(string scenario, string emulatorEndpoint)
        {
            ProjectId = CreateId($"project-{scenario}");
            RoutePrefix = CreateId($"route-{scenario}");

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

        public string RoutePrefix { get; }

        public ZeeqMessagingOptions MessagingOptions { get; } = new();

        public GcpMessagingGatewayConnection Connection { get; }

        public CallSettings CallSettings =>
            CallSettings.FromCancellationToken(CancellationToken.None);

        public string TopicId(string logicalTopic) => $"{RoutePrefix}-{Normalize(logicalTopic)}";

        public string SubscriptionId(string channelName, string logicalTopic) =>
            $"{RoutePrefix}-{Normalize(channelName)}-{Normalize(logicalTopic)}";

        public Google.Cloud.PubSub.V1.TopicName TopicName(string topic) =>
            Google.Cloud.PubSub.V1.TopicName.FromProjectTopic(ProjectId, topic);

        public Google.Cloud.PubSub.V1.SubscriptionName SubscriptionName(string subscription) =>
            Google.Cloud.PubSub.V1.SubscriptionName.FromProjectSubscription(
                ProjectId,
                subscription
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

        public async Task<int> SubscriptionCountAsync()
        {
            var subscriber = await Connection.CreateSubscriberServiceApiClientAsync();
            var count = 0;

            await foreach (
                var _ in subscriber.ListSubscriptionsAsync(
                    $"projects/{ProjectId}",
                    pageToken: null,
                    pageSize: null,
                    CallSettings
                )
            )
            {
                count++;
            }

            return count;
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

        private static string CreateId(string prefix)
        {
            var safePrefix = Normalize(prefix);
            return $"zeeq-{safePrefix}-{Guid.NewGuid():N}"[..48];
        }

        private static string Normalize(string value) => value.Replace('.', '-');

        private static void ConfigureEmulator(PublisherServiceApiClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }

        private static void ConfigureEmulator(PublisherClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }

        private static void ConfigureEmulator(SubscriberServiceApiClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }

        private static void ConfigureEmulator(SubscriberClientBuilder builder)
        {
            builder.EmulatorDetection = EmulatorDetection.EmulatorOnly;
        }
    }

    [ConfigurePublisher("local.setup.proof", visibleTimeoutSeconds: 45, bufferSize: 3)]
    private sealed class LocalSetupMessage(string organizationId, string? teamId)
        : Event(Id.Random()),
            ITenantMessage
    {
        public string OrganizationId { get; } = organizationId;

        public string? TeamId { get; } = teamId;
    }

    [ConfigureConsumer<LocalSetupMessage>(
        channelName: "local.setup.worker",
        noOfPerformers: 2,
        bufferSize: 2,
        visibleTimeoutSeconds: 30,
        pollIntervalMilliseconds: 250
    )]
    private sealed class LocalSetupMessageHandler : RequestHandlerAsync<LocalSetupMessage>
    {
        public override Task<LocalSetupMessage> HandleAsync(
            LocalSetupMessage command,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(command);
    }
}
