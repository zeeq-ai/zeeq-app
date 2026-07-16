using Zeeq.Platform.Messaging;
using Zeeq.Platform.Messaging.GcpPubSub;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub.Tests;

/// <summary>
/// Unit tests for Brighter GCP Pub/Sub adapter metadata generation.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Messaging.GcpPubSub.Tests --output detailed --disable-logo
/// </summary>
public sealed class GcpPubSubRegistryTests
{
    [Test]
    public async Task CreatePublications_WithTenantPublisher_ExpandsRoutesAcrossTierBuckets()
    {
        var registry = new GcpPubSubProducerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            PubSubOptions()
        );

        var publications = registry.CreatePublications();

        await Assert.That(publications).Count().IsEqualTo(6);
        await Assert
            .That(publications.Select(publication => publication.Topic!.Value))
            .Contains("orders.created.priority.00");
        await Assert
            .That(publications.Select(publication => publication.Topic!.Value))
            .Contains("orders.created.default.02");
        await Assert
            .That(publications.Select(publication => publication.Topic!.Value))
            .Contains("orders.created.low.00");
        await Assert
            .That(
                publications.All(publication =>
                    publication.MakeChannels == OnMissingChannel.Validate
                )
            )
            .IsTrue();
        await Assert
            .That(publications.All(publication => publication.EnableMessageOrdering))
            .IsFalse();
        await Assert
            .That(publications.All(publication => publication.TopicAttributes is not null))
            .IsTrue();
        await Assert
            .That(publications.Select(publication => publication.TopicAttributes!.Name))
            .Contains("orders.created.default.02");
        await Assert
            .That(
                publications.All(publication =>
                    publication.TopicAttributes!.Labels[GcpPubSubTopologyLabels.ManagedByKey]
                    == GcpPubSubTopologyLabels.ManagedByValue
                )
            )
            .IsTrue();
        await Assert
            .That(
                publications.All(publication =>
                    publication.TopicAttributes!.ProjectId == "zeeq-test"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task CreatePublications_WithSystemPublisher_UsesSystemRoute()
    {
        var registry = new GcpPubSubProducerRegistry(
            SystemCatalog(),
            MessagingOptions(),
            PubSubOptions()
        );

        var publication = registry.CreatePublications().Single();

        await Assert.That(publication.Topic!.Value).IsEqualTo("system.refresh.system");
        await Assert.That(publication.RequestType).IsEqualTo(typeof(SystemMessage));
        await Assert.That(publication.MakeChannels).IsEqualTo(OnMissingChannel.Validate);
        await Assert
            .That(publication.TopicAttributes!.Labels[GcpPubSubTopologyLabels.ManagedByKey])
            .IsEqualTo(GcpPubSubTopologyLabels.ManagedByValue);
    }

    [Test]
    public async Task CreatePublications_WithCreateMissingChannelPolicy_UsesCreateForProvisioning()
    {
        var registry = new GcpPubSubProducerRegistry(
            SystemCatalog(),
            MessagingOptions(),
            PubSubOptions(missingChannelPolicy: GcpPubSubMissingChannelPolicy.Create)
        );

        var publication = registry.CreatePublications().Single();

        await Assert.That(publication.MakeChannels).IsEqualTo(OnMissingChannel.Create);
    }

    [Test]
    public async Task CreatePublications_WithImmediatePublisher_UsesImmediateRoute()
    {
        var registry = new GcpPubSubProducerRegistry(
            ImmediateCatalog(),
            MessagingOptions(),
            PubSubOptions()
        );

        var publication = registry.CreatePublications().Single();

        await Assert.That(publication.Topic!.Value).IsEqualTo("github.comment.reaction.immediate");
        await Assert.That(publication.RequestType).IsEqualTo(typeof(ImmediateTenantMessage));
    }

    [Test]
    public async Task CreateSubscriptions_WithTenantConsumer_UsesStableSubscriptionIdShape()
    {
        var registry = new GcpPubSubConsumerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            PubSubOptions()
        );

        var subscriptions = registry.CreateSubscriptions();
        var lowSubscription = subscriptions.Single(s =>
            s.RoutingKey.Value == "orders.created.low.00"
        );

        await Assert.That(subscriptions).Count().IsEqualTo(6);
        await Assert
            .That(lowSubscription.Name.Value)
            .IsEqualTo("orders.created.worker.orders.created.low.00");
        await Assert
            .That(lowSubscription.ChannelName.Value)
            .IsEqualTo("orders.created.worker.orders.created.low.00");
        await Assert.That(lowSubscription.RoutingKey.Value).IsEqualTo("orders.created.low.00");
        await Assert.That(lowSubscription.ProjectId).IsEqualTo("zeeq-test");
        await Assert.That(lowSubscription.TopicAttributes!.Name).IsEqualTo("orders.created.low.00");
        await Assert.That(lowSubscription.TopicAttributes.ProjectId).IsEqualTo("zeeq-test");
        await Assert
            .That(lowSubscription.TopicAttributes.Labels[GcpPubSubTopologyLabels.ManagedByKey])
            .IsEqualTo(GcpPubSubTopologyLabels.ManagedByValue);
        await Assert
            .That(lowSubscription.Labels[GcpPubSubTopologyLabels.ManagedByKey])
            .IsEqualTo(GcpPubSubTopologyLabels.ManagedByValue);
        await Assert.That(lowSubscription.DeadLetter).IsNull();
    }

    [Test]
    public async Task CreateSubscriptions_WithConfiguredDefaults_AppliesDefaultsAndOverrides()
    {
        var catalog = new MessagingCatalog(
            [
                new MessagingPublisher(
                    typeof(TenantMessage),
                    "orders.created",
                    typeof(PriorityMessage),
                    VisibleTimeoutSeconds: 0,
                    BufferSize: 0,
                    IsTenantMessage: true,
                    IsSystemMessage: false
                ),
            ],
            [
                new MessagingConsumer(
                    typeof(TenantHandler),
                    typeof(TenantMessage),
                    "orders.created.worker",
                    NoOfPerformers: 0,
                    BufferSize: 0,
                    VisibleTimeoutSeconds: 0,
                    PollIntervalMilliseconds: 0
                ),
            ]
        );
        var registry = new GcpPubSubConsumerRegistry(
            catalog,
            MessagingOptionsWithDefaults(),
            PubSubOptions()
        );

        var subscription = registry
            .CreateSubscriptions()
            .Single(s => s.RoutingKey.Value == "orders.created.priority.00");

        await Assert.That(subscription.NoOfPerformers).IsEqualTo(4);
        await Assert.That(subscription.BufferSize).IsEqualTo(6);
        await Assert.That(subscription.AckDeadlineSeconds).IsEqualTo(75);
        await Assert.That(subscription.TimeOut).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(subscription.EmptyChannelDelay).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(subscription.MessagePumpType).IsEqualTo(MessagePumpType.Proactor);
        await Assert.That(subscription.SubscriptionMode).IsEqualTo(SubscriptionMode.Stream);
    }

    [Test]
    public async Task CreateSubscriptions_WithPullModeOverride_UsesPullMode()
    {
        var registry = new GcpPubSubConsumerRegistry(
            ImmediateCatalog(),
            MessagingOptions(),
            PubSubOptions(subscriptionMode: SubscriptionMode.Pull)
        );

        var subscription = registry.CreateSubscriptions().Single();

        await Assert.That(subscription.SubscriptionMode).IsEqualTo(SubscriptionMode.Pull);
        await Assert.That(subscription.MessagePumpType).IsEqualTo(MessagePumpType.Proactor);
    }

    [Test]
    public async Task CreateSubscriptions_WithAckDeadlineOverride_ClampsToPubSubRange()
    {
        var lowRegistry = new GcpPubSubConsumerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            PubSubOptions(ackDeadlineSeconds: 1)
        );
        var highRegistry = new GcpPubSubConsumerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            PubSubOptions(ackDeadlineSeconds: 1000)
        );

        var lowDeadline = lowRegistry.CreateSubscriptions().First().AckDeadlineSeconds;
        var highDeadline = highRegistry.CreateSubscriptions().First().AckDeadlineSeconds;

        await Assert.That(lowDeadline).IsEqualTo(10);
        await Assert.That(highDeadline).IsEqualTo(600);
    }

    [Test]
    public async Task CreateSubscriptions_WithLabelsAndOrderingOptions_MapsPubSubFields()
    {
        var registry = new GcpPubSubConsumerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            PubSubOptions(
                enableMessageOrdering: true,
                enableExactlyOnceDelivery: true,
                labels: new Dictionary<string, string> { ["app"] = "zeeq" }
            )
        );

        var subscription = registry.CreateSubscriptions().First();

        await Assert.That(subscription.EnableMessageOrdering).IsTrue();
        await Assert.That(subscription.EnableExactlyOnceDelivery).IsTrue();
        await Assert.That(subscription.Labels["app"]).IsEqualTo("zeeq");
        await Assert
            .That(subscription.Labels[GcpPubSubTopologyLabels.ManagedByKey])
            .IsEqualTo(GcpPubSubTopologyLabels.ManagedByValue);
    }

    [Test]
    public async Task TopologyManifest_WithGeneratedDescriptors_ContainsManagedResources()
    {
        var pubSubOptions = PubSubOptions();
        var publications = new GcpPubSubProducerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            pubSubOptions
        ).CreatePublications();
        var subscriptions = new GcpPubSubConsumerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            pubSubOptions
        ).CreateSubscriptions();

        var manifest = GcpPubSubTopologyManifest.Create(pubSubOptions, publications, subscriptions);

        await Assert.That(manifest.Topics).Count().IsEqualTo(6);
        await Assert.That(manifest.Subscriptions).Count().IsEqualTo(6);
        await Assert.That(manifest.Topics.All(topic => topic.ProjectId == "zeeq-test")).IsTrue();
        await Assert
            .That(
                manifest.Topics.All(topic =>
                    topic.Labels[GcpPubSubTopologyLabels.ManagedByKey]
                    == GcpPubSubTopologyLabels.ManagedByValue
                )
            )
            .IsTrue();
        await Assert
            .That(
                manifest.Subscriptions.All(subscription =>
                    subscription.Labels[GcpPubSubTopologyLabels.ManagedByKey]
                    == GcpPubSubTopologyLabels.ManagedByValue
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task TopologyPlanner_WithPartialInventory_ReturnsOnlyMissingResources()
    {
        var pubSubOptions = PubSubOptions();
        var publications = new GcpPubSubProducerRegistry(
            SystemCatalog(),
            MessagingOptions(),
            pubSubOptions
        ).CreatePublications();
        var subscriptions = new GcpPubSubConsumerRegistry(
            ImmediateCatalog(),
            MessagingOptions(),
            pubSubOptions
        ).CreateSubscriptions();
        var manifest = GcpPubSubTopologyManifest.Create(pubSubOptions, publications, subscriptions);
        var existingTopics = manifest.Topics.Take(1).Select(topic => topic.Identity).ToHashSet();
        var existingSubscriptions = new HashSet<GcpPubSubSubscriptionIdentity>();

        var plan = GcpPubSubTopologyPlanner.CreatePlan(
            manifest,
            existingTopics,
            existingSubscriptions
        );

        await Assert.That(plan.TopicsToCreate).Count().IsEqualTo(manifest.Topics.Count - 1);
        await Assert
            .That(plan.SubscriptionsToCreate)
            .Count()
            .IsEqualTo(manifest.Subscriptions.Count);
    }

    [Test]
    public async Task ResourceNameValidator_WithInvalidNames_ThrowsControlledException()
    {
        var validator = new GcpPubSubResourceNameValidator();

        await Assert
            .That(() => validator.ValidateTopic("googaudit"))
            .Throws<GcpPubSubResourceNameException>();
        await Assert
            .That(() => validator.ValidateTopic("ab"))
            .Throws<GcpPubSubResourceNameException>();
        await Assert
            .That(() => validator.ValidateTopic("orders*created"))
            .Throws<GcpPubSubResourceNameException>();
        await Assert
            .That(() => validator.ValidateTopic("1orders.created"))
            .Throws<GcpPubSubResourceNameException>();
    }

    [Test]
    public async Task ResourceNameValidator_WithProductionRouteShapes_AcceptsNames()
    {
        var validator = new GcpPubSubResourceNameValidator();
        var topicIds = new[]
        {
            "code-review.run.default.03",
            "github.webhook.pull-request.priority.00",
            "diagnostics.message-queue.smoke-test.system",
        };

        foreach (var topicId in topicIds)
        {
            validator.ValidateTopic(topicId);
        }

        validator.ValidateSubscription("code-review.run.worker.code-review.run.default.03");

        await Assert.That(topicIds).Count().IsEqualTo(3);
    }

    private static MessagingCatalog TenantCatalog() =>
        new(
            [
                new MessagingPublisher(
                    typeof(TenantMessage),
                    "orders.created",
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 10,
                    IsTenantMessage: true,
                    IsSystemMessage: false
                ),
            ],
            [
                new MessagingConsumer(
                    typeof(TenantHandler),
                    typeof(TenantMessage),
                    "orders.created.worker",
                    NoOfPerformers: 2,
                    BufferSize: 5,
                    VisibleTimeoutSeconds: 60,
                    PollIntervalMilliseconds: 750
                ),
            ]
        );

    private static MessagingCatalog SystemCatalog() =>
        new(
            [
                new MessagingPublisher(
                    typeof(SystemMessage),
                    "system.refresh",
                    typeof(DefaultMessage),
                    VisibleTimeoutSeconds: 30,
                    BufferSize: 10,
                    IsTenantMessage: false,
                    IsSystemMessage: true
                ),
            ],
            []
        );

    private static MessagingCatalog ImmediateCatalog() =>
        new(
            [
                new MessagingPublisher(
                    typeof(ImmediateTenantMessage),
                    "github.comment.reaction",
                    typeof(ImmediateMessage),
                    VisibleTimeoutSeconds: 0,
                    BufferSize: 0,
                    IsTenantMessage: true,
                    IsSystemMessage: false
                ),
            ],
            [
                new MessagingConsumer(
                    typeof(ImmediateTenantHandler),
                    typeof(ImmediateTenantMessage),
                    "github.comment.reaction.worker",
                    NoOfPerformers: 0,
                    BufferSize: 0,
                    VisibleTimeoutSeconds: 0,
                    PollIntervalMilliseconds: 0
                ),
            ]
        );

    private static ZeeqMessagingOptions MessagingOptions() =>
        new()
        {
            TenantBuckets = new TenantBucketRoutingOptions
            {
                PriorityBucketCount = 2,
                DefaultBucketCount = 3,
                LowBucketCount = 1,
            },
        };

    private static ZeeqMessagingOptions MessagingOptionsWithDefaults() =>
        new()
        {
            Defaults = new MessagingDefaultsOptions
            {
                BufferSize = 3,
                NoOfPerformers = 1,
                VisibleTimeoutSeconds = 45,
                PollIntervalMilliseconds = 900,
            },
            PriorityDefaults = new Dictionary<Type, MessagingDefaultsOptions>
            {
                [typeof(PriorityMessage)] = new()
                {
                    NoOfPerformers = 4,
                    PollIntervalMilliseconds = 250,
                },
                [typeof(DefaultMessage)] = new(),
                [typeof(LowPriorityMessage)] = new() { PollIntervalMilliseconds = 2000 },
            },
            TopicOverrides = new Dictionary<string, MessagingDefaultsOptions>
            {
                ["orders.created"] = new() { BufferSize = 6, VisibleTimeoutSeconds = 75 },
            },
            TenantBuckets = new TenantBucketRoutingOptions
            {
                PriorityBucketCount = 1,
                DefaultBucketCount = 1,
                LowBucketCount = 1,
            },
        };

    private static GcpPubSubMessagingOptions PubSubOptions(
        SubscriptionMode subscriptionMode = SubscriptionMode.Stream,
        int? ackDeadlineSeconds = null,
        GcpPubSubMissingChannelPolicy missingChannelPolicy = GcpPubSubMissingChannelPolicy.Validate,
        bool enableMessageOrdering = false,
        bool enableExactlyOnceDelivery = false,
        Dictionary<string, string>? labels = null
    ) =>
        new()
        {
            ProjectId = "zeeq-test",
            SubscriptionMode = subscriptionMode,
            AckDeadlineSeconds = ackDeadlineSeconds,
            MissingChannelPolicy = missingChannelPolicy,
            EnableMessageOrdering = enableMessageOrdering,
            EnableExactlyOnceDelivery = enableExactlyOnceDelivery,
            Labels = labels ?? [],
        };

    private sealed class TenantMessage;

    private sealed class SystemMessage;

    private sealed class ImmediateTenantMessage;

    private sealed class TenantHandler;

    private sealed class ImmediateTenantHandler;
}
