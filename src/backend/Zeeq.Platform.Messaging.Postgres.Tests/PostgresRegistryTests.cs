using Zeeq.Core.Models;
using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.Postgres.Tests;

/// <summary>
/// Unit tests for Brighter Postgres adapter metadata generation.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Messaging.Postgres.Tests --output detailed --disable-logo
/// </summary>
public sealed class PostgresRegistryTests
{
    [Test]
    public async Task CreatePublications_WithTenantPublisher_ExpandsRoutesAcrossTierBuckets()
    {
        var registry = new PostgresProducerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            PostgresOptions()
        );

        var publications = registry.CreatePublications();

        await Assert.That(publications).Count().IsEqualTo(6);
        await Assert
            .That(
                publications.Count(publication => publication.QueueStoreTable == "queue_priority")
            )
            .IsEqualTo(2);
        await Assert
            .That(publications.Count(publication => publication.QueueStoreTable == "queue_default"))
            .IsEqualTo(3);
        await Assert
            .That(publications.Count(publication => publication.QueueStoreTable == "queue_low"))
            .IsEqualTo(1);
        await Assert
            .That(publications.Select(publication => publication.Topic!.Value))
            .Contains("orders.created.priority.00");
        await Assert
            .That(publications.Select(publication => publication.Topic!.Value))
            .Contains("orders.created.default.02");
        await Assert
            .That(publications.Select(publication => publication.Topic!.Value))
            .Contains("orders.created.low.00");
    }

    [Test]
    public async Task CreatePublications_WithSystemPublisher_UsesSystemQueueTable()
    {
        var catalog = new MessagingCatalog(
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
        var registry = new PostgresProducerRegistry(catalog, MessagingOptions(), PostgresOptions());

        var publication = registry.CreatePublications().Single();

        await Assert.That(publication.Topic!.Value).IsEqualTo("system.refresh.system");
        await Assert.That(publication.QueueStoreTable).IsEqualTo("queue_system");
        await Assert.That(publication.SchemaName).IsEqualTo("messaging_test");
        await Assert.That(publication.BinaryMessagePayload).IsTrue();
    }

    [Test]
    public async Task CreatePublications_WithImmediatePublisher_UsesImmediateQueueTable()
    {
        var registry = new PostgresProducerRegistry(
            ImmediateCatalog(),
            MessagingOptions(),
            PostgresOptions()
        );

        var publication = registry.CreatePublications().Single();

        await Assert.That(publication.Topic!.Value).IsEqualTo("github.comment.reaction.immediate");
        await Assert.That(publication.QueueStoreTable).IsEqualTo("queue_immediate");
        await Assert.That(publication.SchemaName).IsEqualTo("messaging_test");
    }

    [Test]
    public async Task CreateSubscriptions_WithTenantConsumer_ExpandsRoutesWithoutNativeDeadLetter()
    {
        var registry = new PostgresConsumerRegistry(
            TenantCatalog(),
            MessagingOptions(),
            PostgresOptions()
        );

        var subscriptions = registry.CreateSubscriptions();

        await Assert.That(subscriptions).Count().IsEqualTo(6);
        await Assert
            .That(subscriptions.Select(subscription => subscription.RoutingKey.Value))
            .Contains("orders.created.priority.01");
        await Assert
            .That(subscriptions.Select(subscription => subscription.RoutingKey.Value))
            .Contains("orders.created.default.02");
        await Assert
            .That(subscriptions.All(subscription => subscription.DeadLetterRoutingKey is null))
            .IsTrue();
        await Assert
            .That(subscriptions.All(subscription => subscription.InvalidMessageRoutingKey is null))
            .IsTrue();
        await Assert
            .That(
                subscriptions
                    .Single(s => s.RoutingKey.Value == "orders.created.low.00")
                    .QueueStoreTable
            )
            .IsEqualTo("queue_low");
    }

    [Test]
    public async Task CreateSubscriptions_WithConfiguredDefaults_AppliesPriorityAndTopicOverrides()
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
        var registry = new PostgresConsumerRegistry(
            catalog,
            MessagingOptionsWithDefaults(),
            PostgresOptions()
        );

        var subscription = registry
            .CreateSubscriptions()
            .Single(s => s.RoutingKey.Value == "orders.created.priority.00");

        await Assert.That(subscription.NoOfPerformers).IsEqualTo(4);
        await Assert.That(subscription.BufferSize).IsEqualTo(6);
        await Assert.That(subscription.TimeOut).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(subscription.EmptyChannelDelay).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(subscription.VisibleTimeout).IsEqualTo(TimeSpan.FromSeconds(75));
    }

    [Test]
    public async Task CreateSubscriptions_WithImmediateConsumer_UsesImmediateDefaultsAndSingleRoute()
    {
        var registry = new PostgresConsumerRegistry(
            ImmediateCatalog(),
            MessagingOptions(),
            PostgresOptions()
        );

        var subscription = registry.CreateSubscriptions().Single();

        await Assert
            .That(subscription.RoutingKey.Value)
            .IsEqualTo("github.comment.reaction.immediate");
        await Assert.That(subscription.QueueStoreTable).IsEqualTo("queue_immediate");
        await Assert.That(subscription.BufferSize).IsEqualTo(10);
        await Assert.That(subscription.NoOfPerformers).IsEqualTo(16);
        await Assert.That(subscription.TimeOut).IsEqualTo(TimeSpan.FromMilliseconds(50));
        await Assert.That(subscription.EmptyChannelDelay).IsEqualTo(TimeSpan.FromMilliseconds(50));
        await Assert.That(subscription.VisibleTimeout).IsEqualTo(TimeSpan.FromSeconds(60));
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

    private static PostgresMessagingOptions PostgresOptions() =>
        new()
        {
            SchemaName = "messaging_test",
            QueueTables = new PostgresQueueTableOptions
            {
                Priority = "queue_priority",
                Default = "queue_default",
                Low = "queue_low",
                System = "queue_system",
                Immediate = "queue_immediate",
            },
        };

    private sealed class TenantMessage;

    private sealed class SystemMessage;

    private sealed class ImmediateTenantMessage;

    private sealed class TenantHandler;

    private sealed class ImmediateTenantHandler;
}
