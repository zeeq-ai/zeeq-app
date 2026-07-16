using Zeeq.Testing;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub.Tests.Integration;

/// <summary>
/// Explicit emulator tests for Zeeq-owned Pub/Sub topology reconciliation.
/// </summary>
/// <remarks>
/// Run:
/// <code>
/// dotnet run --project src/backend/Zeeq.Platform.Messaging.GcpPubSub.Tests.Integration/Zeeq.Platform.Messaging.GcpPubSub.Tests.Integration.csproj -- --treenode-filter "/*/*/*/*[Category=PubSubEmulator]" --output detailed --disable-logo
/// </code>
///
/// These tests are marked explicit because they start a Docker-backed Pub/Sub
/// emulator through Testcontainers. They verify real Google Pub/Sub management
/// client behavior and should not run as part of the normal fast unit test pass.
/// </remarks>
[Explicit]
[Category("PubSubEmulator")]
[ClassDataSource<GcpPubSubFixture>(Shared = SharedType.PerTestSession)]
public sealed class GcpPubSubTopologyServiceEmulatorTests(GcpPubSubFixture fixture)
{
    [Test]
    public async Task EnsureTopologyAsync_WithMissingArtifacts_CreatesTopicsAndSubscriptions()
    {
        var context = CreateContext("topology-create");
        var manifest = context.CreateManifest();
        var service = context.CreateService(manifest);

        await service.EnsureTopologyAsync(CancellationToken.None);

        await Assert.That(await context.TopicCountAsync()).IsEqualTo(manifest.Topics.Count);
        await Assert
            .That(await context.SubscriptionCountAsync())
            .IsEqualTo(manifest.Subscriptions.Count);

        foreach (var topic in manifest.Topics)
        {
            var gcpTopic = await context.GetTopicAsync(topic.TopicId);
            await Assert
                .That(gcpTopic.Labels[GcpPubSubTopologyLabels.ManagedByKey])
                .IsEqualTo(GcpPubSubTopologyLabels.ManagedByValue);
        }

        foreach (var subscription in manifest.Subscriptions)
        {
            var gcpSubscription = await context.GetSubscriptionAsync(subscription.SubscriptionId);
            await Assert
                .That(gcpSubscription.Labels[GcpPubSubTopologyLabels.ManagedByKey])
                .IsEqualTo(GcpPubSubTopologyLabels.ManagedByValue);
        }
    }

    [Test]
    public async Task EnsureTopologyAsync_WithExistingArtifacts_IsIdempotent()
    {
        var context = CreateContext("topology-idempotent");
        var manifest = context.CreateManifest();
        var service = context.CreateService(manifest);

        await service.EnsureTopologyAsync(CancellationToken.None);
        await service.EnsureTopologyAsync(CancellationToken.None);

        await Assert.That(await context.TopicCountAsync()).IsEqualTo(manifest.Topics.Count);
        await Assert
            .That(await context.SubscriptionCountAsync())
            .IsEqualTo(manifest.Subscriptions.Count);
    }

    private TopologyPubSubContext CreateContext(string scenario) =>
        new(scenario, fixture.PubSubContainer.GetEmulatorEndpoint());

    private sealed class TopologyPubSubContext
    {
        public TopologyPubSubContext(string scenario, string emulatorEndpoint)
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];

            ProjectId = CreateId($"project-{scenario}");
            TopicBase = $"topology.{scenario}.{suffix}";
            ChannelName = $"topology.{scenario}.worker.{suffix}";

            Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", emulatorEndpoint);

            Options = new GcpPubSubMessagingOptions
            {
                ProjectId = ProjectId,
                SubscriptionMode = SubscriptionMode.Pull,
                MissingChannelPolicy = GcpPubSubMissingChannelPolicy.Validate,
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

        public GcpPubSubTopologyManifest CreateManifest()
        {
            var catalog = Catalog();
            var messagingOptions = MessagingOptions();
            var publications = new GcpPubSubProducerRegistry(
                catalog,
                messagingOptions,
                Options
            ).CreatePublications();
            var subscriptions = new GcpPubSubConsumerRegistry(
                catalog,
                messagingOptions,
                Options
            ).CreateSubscriptions();

            return GcpPubSubTopologyManifest.Create(Options, publications, subscriptions);
        }

        public GcpPubSubTopologyService CreateService(GcpPubSubTopologyManifest manifest) =>
            new(Connection, manifest, NullLogger<GcpPubSubTopologyService>.Instance);

        public async Task<Topic> GetTopicAsync(string topic)
        {
            var publisher = await Connection.CreatePublisherServiceApiClientAsync();

            return await publisher.GetTopicAsync(TopicName(topic), CallSettings);
        }

        public async Task<Google.Cloud.PubSub.V1.Subscription> GetSubscriptionAsync(
            string subscription
        )
        {
            var subscriber = await Connection.CreateSubscriberServiceApiClientAsync();

            return await subscriber.GetSubscriptionAsync(
                SubscriptionName(subscription),
                CallSettings
            );
        }

        public async Task<int> TopicCountAsync()
        {
            var publisher = await Connection.CreatePublisherServiceApiClientAsync();
            var count = 0;

            await foreach (
                var _ in publisher.ListTopicsAsync(
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

        private Google.Cloud.PubSub.V1.TopicName TopicName(string topic) =>
            Google.Cloud.PubSub.V1.TopicName.FromProjectTopic(ProjectId, topic);

        private Google.Cloud.PubSub.V1.SubscriptionName SubscriptionName(string subscription) =>
            Google.Cloud.PubSub.V1.SubscriptionName.FromProjectSubscription(
                ProjectId,
                subscription
            );

        private MessagingCatalog Catalog() =>
            new(
                [
                    new MessagingPublisher(
                        typeof(TopologyTenantMessage),
                        TopicBase,
                        typeof(DefaultMessage),
                        VisibleTimeoutSeconds: 30,
                        BufferSize: 2,
                        IsTenantMessage: true,
                        IsSystemMessage: false
                    ),
                ],
                [
                    new MessagingConsumer(
                        typeof(TopologyTenantMessageHandler),
                        typeof(TopologyTenantMessage),
                        ChannelName,
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

        private static string CreateId(string prefix)
        {
            var safePrefix = prefix.Replace('.', '-');
            return $"zeeq-{safePrefix}-{Guid.NewGuid():N}"[..48];
        }
    }

    private sealed class TopologyTenantMessage : Event, ITenantMessage
    {
        public TopologyTenantMessage()
            : base(Id.Random()) { }

        public string OrganizationId { get; } = "org_topology";

        public string? TeamId { get; } = null;
    }

    private sealed class TopologyTenantMessageHandler : RequestHandlerAsync<TopologyTenantMessage>
    {
        public override Task<TopologyTenantMessage> HandleAsync(
            TopologyTenantMessage command,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(command);
    }
}
