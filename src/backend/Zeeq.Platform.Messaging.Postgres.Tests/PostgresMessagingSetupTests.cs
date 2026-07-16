using Microsoft.Extensions.DependencyInjection;
using Paramore.Brighter;
using Paramore.Brighter.Extensions.DependencyInjection;
using Paramore.Brighter.MessageMappers;

namespace Zeeq.Platform.Messaging.Postgres.Tests;

public sealed class PostgresMessagingSetupTests
{
    [Test]
    public async Task AddZeeqPostgresMessageProducers_UsesJsonMapperAsDefaultMapper()
    {
        var services = new ServiceCollection();

        services.AddZeeqPostgresMessageProducers(
            "Host=localhost;Database=zeeq_test;Username=zeeq;Password=password",
            MessagingOptions(),
            PostgresOptions(),
            typeof(PostgresMessagingSetupTests).Assembly
        );

        using var provider = services.BuildServiceProvider();
        var mapperRegistry =
            provider.GetRequiredService<ServiceCollectionMessageMapperRegistryBuilder>();

        await Assert
            .That(mapperRegistry.DefaultMessageMapper)
            .IsEqualTo(typeof(JsonMessageMapper<>));
        await Assert
            .That(mapperRegistry.DefaultMessageMapperAsync)
            .IsEqualTo(typeof(JsonMessageMapper<>));
    }

    [Test]
    public async Task AddZeeqPostgresMessageProducers_RegistersPublisherAsScoped()
    {
        var services = new ServiceCollection();

        services.AddZeeqPostgresMessageProducers(
            "Host=localhost;Database=zeeq_test;Username=zeeq;Password=password",
            MessagingOptions(),
            PostgresOptions(),
            typeof(PostgresMessagingSetupTests).Assembly
        );

        var descriptor = services.Single(service =>
            service.ServiceType == typeof(IZeeqMessagePublisher)
        );

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
    }

    [Test]
    public async Task JsonMessageMapper_RoundTripsMessagePayloadProperties()
    {
        var request = new PayloadTenantMessage
        {
            OrganizationId = "org_123",
            TeamId = "team_456",
            PayloadValue = "feature-owned-payload",
            Revision = 7,
        };
        var mapper = new JsonMessageMapper<PayloadTenantMessage> { Context = new RequestContext() };

        var message = mapper.MapToMessage(
            request,
            new Publication { Topic = new RoutingKey("payload.created.default.00") }
        );
        var roundTrip = mapper.MapToRequest(message);

        await Assert.That(roundTrip.OrganizationId).IsEqualTo("org_123");
        await Assert.That(roundTrip.TeamId).IsEqualTo("team_456");
        await Assert.That(roundTrip.PayloadValue).IsEqualTo("feature-owned-payload");
        await Assert.That(roundTrip.Revision).IsEqualTo(7);
    }

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
            },
        };

    [ConfigurePublisher("payload.created")]
    private sealed class PayloadTenantMessage : Event, ITenantMessage
    {
        public PayloadTenantMessage()
            : base(Id.Random()) { }

        public string OrganizationId { get; init; } = string.Empty;

        public string? TeamId { get; init; }

        public string PayloadValue { get; init; } = string.Empty;

        public int Revision { get; init; }
    }
}
