using Zeeq.Testing;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub.Tests.Integration;

/// <summary>
/// Explicit emulator tests for Zeeq's Pub/Sub adapter metadata.
/// </summary>
/// <remarks>
/// Phase 0 proves Brighter's raw GCP primitives. These tests start from the
/// production Zeeq Pub/Sub registries and prove that their generated metadata
/// can provision emulator resources and round-trip a message.
/// </remarks>
[Explicit]
[Category("PubSubEmulator")]
[ClassDataSource<GcpPubSubFixture>(Shared = SharedType.PerTestSession)]
public sealed class GcpPubSubAdapterEmulatorTests(GcpPubSubFixture fixture)
{
    [Test]
    public async Task AdapterRegistries_WithTenantCatalog_ProvisionGeneratedRoutes()
    {
        var context = CreateContext("adapter-provision");
        var catalog = TenantCatalog(context.TopicBase, context.ChannelName);
        var publications = new GcpPubSubProducerRegistry(
            catalog,
            MessagingOptions(),
            context.Options
        ).CreatePublications();
        var subscriptions = new GcpPubSubConsumerRegistry(
            catalog,
            MessagingOptions(),
            context.Options
        ).CreateSubscriptions();

        using var registry = await new GcpPubSubProducerRegistryFactory(
            context.Connection,
            publications
        ).CreateAsync(CancellationToken.None);
        await using var channels = await AdapterChannels.CreateAsync(
            context.Connection,
            subscriptions
        );

        await Assert.That(publications).Count().IsEqualTo(3);
        await Assert.That(subscriptions).Count().IsEqualTo(3);
        await Assert
            .That(publications.Select(publication => publication.Topic!.Value))
            .Contains($"{context.TopicBase}.default.00");

        foreach (var publication in publications)
        {
            await Assert.That(await context.TopicExistsAsync(publication.Topic!.Value)).IsTrue();
        }

        foreach (var subscription in subscriptions)
        {
            await Assert
                .That(await context.SubscriptionExistsAsync(subscription.Name.Value))
                .IsTrue();
            await Assert.That(subscription.SubscriptionMode).IsEqualTo(SubscriptionMode.Pull);
            await Assert.That(subscription.MessagePumpType).IsEqualTo(MessagePumpType.Proactor);
        }

        registry.CloseAll();
    }

    [Test]
    public async Task AdapterGeneratedMetadata_CanRoundTripSystemMessage()
    {
        var context = CreateContext("adapter-roundtrip");
        var catalog = SystemCatalog(context.TopicBase, context.ChannelName);
        var publications = new GcpPubSubProducerRegistry(
            catalog,
            MessagingOptions(),
            context.Options
        ).CreatePublications();
        var subscriptions = new GcpPubSubConsumerRegistry(
            catalog,
            MessagingOptions(),
            context.Options
        ).CreateSubscriptions();
        var payload = $"adapter-payload-{Guid.NewGuid():N}";

        await using var channels = await AdapterChannels.CreateAsync(
            context.Connection,
            subscriptions
        );
        using var registry = await new GcpPubSubProducerRegistryFactory(
            context.Connection,
            publications
        ).CreateAsync(CancellationToken.None);

        var topic = publications.Single().Topic!;
        var channel = channels.Single();

        await registry
            .LookupAsyncBy(topic)
            .SendAsync(context.Message(topic.Value, payload), CancellationToken.None);

        var received = await context.ReceiveNonEmptyAsync(channel);
        await channel.AcknowledgeAsync(received, CancellationToken.None);

        await Assert.That(received.Header.Topic.Value).IsEqualTo(topic.Value);
        await Assert.That(received.Body.Value).IsEqualTo(payload);
    }

    private AdapterPubSubContext CreateContext(string scenario) =>
        new(scenario, fixture.PubSubContainer.GetEmulatorEndpoint());

    private static MessagingCatalog TenantCatalog(string topic, string channelName) =>
        new(
            [
                new MessagingPublisher(
                    typeof(AdapterTenantMessage),
                    topic,
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 2,
                    IsTenantMessage: true,
                    IsSystemMessage: false
                ),
            ],
            [
                new MessagingConsumer(
                    typeof(AdapterTenantMessageHandler),
                    typeof(AdapterTenantMessage),
                    channelName,
                    NoOfPerformers: 1,
                    BufferSize: 1,
                    VisibleTimeoutSeconds: 30,
                    PollIntervalMilliseconds: 100
                ),
            ]
        );

    private static MessagingCatalog SystemCatalog(string topic, string channelName) =>
        new(
            [
                new MessagingPublisher(
                    typeof(AdapterSystemMessage),
                    topic,
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 2,
                    IsTenantMessage: false,
                    IsSystemMessage: true
                ),
            ],
            [
                new MessagingConsumer(
                    typeof(AdapterSystemMessageHandler),
                    typeof(AdapterSystemMessage),
                    channelName,
                    NoOfPerformers: 1,
                    BufferSize: 1,
                    VisibleTimeoutSeconds: 30,
                    PollIntervalMilliseconds: 100
                ),
            ]
        );

    private static ZeeqMessagingOptions MessagingOptions() =>
        new()
        {
            TenantBuckets = new TenantBucketRoutingOptions
            {
                PriorityBucketCount = 1,
                DefaultBucketCount = 1,
                LowBucketCount = 1,
            },
        };

    private sealed record AdapterChannels(IReadOnlyList<IAmAChannelAsync> Channels)
        : IAsyncDisposable
    {
        public static async Task<AdapterChannels> CreateAsync(
            GcpMessagingGatewayConnection connection,
            IReadOnlyList<GcpPubSubSubscription> subscriptions
        )
        {
            var channelFactory = new GcpPubSubChannelFactory(connection);
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

            return new(channels);
        }

        public IAmAChannelAsync Single() => Channels.Single();

        public async ValueTask DisposeAsync()
        {
            foreach (var channel in Channels)
            {
                await channel.DisposeAsync();
            }
        }
    }

    private sealed class AdapterPubSubContext
    {
        public AdapterPubSubContext(string scenario, string emulatorEndpoint)
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];

            ProjectId = CreateId($"project-{scenario}");
            TopicBase = $"adapter.{scenario}.{suffix}";
            ChannelName = $"adapter.{scenario}.worker.{suffix}";

            Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", emulatorEndpoint);

            Options = new GcpPubSubMessagingOptions
            {
                ProjectId = ProjectId,
                SubscriptionMode = SubscriptionMode.Pull,
                MissingChannelPolicy = GcpPubSubMissingChannelPolicy.Create,
            };
            Connection = GcpPubSubGatewayConnectionFactory.Create(Options);
        }

        public string ProjectId { get; }

        public string TopicBase { get; }

        public string ChannelName { get; }

        public GcpPubSubMessagingOptions Options { get; }

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
            var safePrefix = prefix.Replace('.', '-');
            return $"zeeq-{safePrefix}-{Guid.NewGuid():N}"[..48];
        }
    }

    private sealed class AdapterTenantMessage : Event, ITenantMessage
    {
        public AdapterTenantMessage()
            : base(Id.Random()) { }

        public string OrganizationId { get; } = "org_adapter";

        public string? TeamId { get; } = null;
    }

    private sealed class AdapterSystemMessage : Event, ISystemMessage
    {
        public AdapterSystemMessage()
            : base(Id.Random()) { }
    }

    private sealed class AdapterTenantMessageHandler : RequestHandlerAsync<AdapterTenantMessage>
    {
        public override Task<AdapterTenantMessage> HandleAsync(
            AdapterTenantMessage command,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(command);
    }

    private sealed class AdapterSystemMessageHandler : RequestHandlerAsync<AdapterSystemMessage>
    {
        public override Task<AdapterSystemMessage> HandleAsync(
            AdapterSystemMessage command,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(command);
    }
}
