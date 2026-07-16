using Zeeq.Core.Models;
using Zeeq.Platform.Messaging.Postgres;
using Zeeq.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paramore.Brighter;
using Paramore.Brighter.Extensions.DependencyInjection;
using Paramore.Brighter.MessageMappers;
using Paramore.Brighter.ServiceActivator;

namespace Zeeq.Platform.Messaging.GcpPubSub.Tests;

/// <summary>
/// Unit tests for GCP Pub/Sub messaging service registration.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Messaging.GcpPubSub.Tests --output detailed --disable-logo
/// </summary>
[SkipInCi(
    "Do not run GCP Pub/Sub messaging tests in CI because the emulator requires service account credentials."
)]
public sealed class GcpPubSubMessagingSetupTests
{
    [Test]
    public async Task AddZeeqGcpPubSubMessageProducers_UsesJsonMapperAsDefaultMapper()
    {
        var services = new ServiceCollection();

        RegisterProducerSetup(services);

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
    public async Task AddZeeqGcpPubSubMessageProducers_RegistersPublisherAsScoped()
    {
        var services = new ServiceCollection();

        RegisterProducerSetup(services);

        var descriptor = services.Single(service =>
            service.ServiceType == typeof(IZeeqMessagePublisher)
        );

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert
            .That(descriptor.ImplementationType)
            .IsEqualTo(typeof(GcpPubSubZeeqMessagePublisher));
    }

    [Test]
    public async Task AddZeeqGcpPubSubMessageProducers_ResolvesPublisherWithoutContactingGcp()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantTierResolver, TestTenantTierResolver>();

        RegisterProducerSetup(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IZeeqMessagePublisher>();

        await Assert.That(publisher).IsAssignableTo<GcpPubSubZeeqMessagePublisher>();
    }

    [Test]
    public async Task AddZeeqGcpPubSubMessageConsumers_ResolvesDispatcherWithoutContactingGcp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantTierResolver, TestTenantTierResolver>();

        RegisterConsumerSetup(services);

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var consumerOptions = provider.GetRequiredService<IAmConsumerOptions>();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        await Assert.That(dispatcher).IsNotNull();
        await Assert.That(consumerOptions.Subscriptions).IsNotEmpty();
        await Assert
            .That(hostedServices.Any(service => service is BrighterMessagingConsumerHostedService))
            .IsTrue();
        await Assert
            .That(
                hostedServices.Any(service =>
                    service.GetType().Name == "GcpPubSubMessagingStartupLogger"
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task AddZeeqGcpPubSubMessageConsumers_RegistersPostgresDeadLetterWriter()
    {
        var services = new ServiceCollection();

        RegisterConsumerSetup(services);

        var descriptor = services.Single(service =>
            service.ServiceType == typeof(IDeadLetterWriter)
        );

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    [Test]
    public async Task AddZeeqGcpPubSubMessaging_WithInlineConsumers_RegistersProducerAndConsumerServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ITenantTierResolver, TestTenantTierResolver>();

        RegisterCombinedSetup(services, registerConsumers: true);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IZeeqMessagePublisher>();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var consumerOptions = provider.GetRequiredService<IAmConsumerOptions>();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();

        await Assert.That(publisher).IsAssignableTo<GcpPubSubZeeqMessagePublisher>();
        await Assert.That(dispatcher).IsNotNull();
        await Assert.That(consumerOptions.Subscriptions).IsNotEmpty();
        await Assert
            .That(hostedServices.Any(service => service is BrighterMessagingConsumerHostedService))
            .IsTrue();
    }

    private static void RegisterProducerSetup(IServiceCollection services)
    {
        services.AddZeeqGcpPubSubMessageProducers(
            "Host=localhost;Database=zeeq_test;Username=zeeq;Password=password",
            MessagingOptions(),
            PubSubOptions(),
            DeadLetterOptions(),
            typeof(GcpPubSubMessagingSetupTests).Assembly
        );
    }

    private static void RegisterConsumerSetup(IServiceCollection services)
    {
        services.AddZeeqGcpPubSubMessageConsumers(
            "Host=localhost;Database=zeeq_test;Username=zeeq;Password=password",
            MessagingOptions(),
            PubSubOptions(),
            DeadLetterOptions(),
            typeof(GcpPubSubMessagingSetupTests).Assembly
        );
    }

    private static void RegisterCombinedSetup(IServiceCollection services, bool registerConsumers)
    {
        services.AddZeeqGcpPubSubMessaging(
            "Host=localhost;Database=zeeq_test;Username=zeeq;Password=password",
            MessagingOptions(),
            PubSubOptions(),
            DeadLetterOptions(),
            registerConsumers,
            typeof(GcpPubSubMessagingSetupTests).Assembly
        );
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

    private static GcpPubSubMessagingOptions PubSubOptions() =>
        new()
        {
            ProjectId = "zeeq-test",
            MissingChannelPolicy = GcpPubSubMissingChannelPolicy.Assume,
        };

    private static PostgresMessagingOptions DeadLetterOptions() =>
        new() { SchemaName = "messaging_test" };

    [ConfigurePublisher("setup.payload.created")]
    private sealed class SetupTenantMessage : Event, ITenantMessage
    {
        public SetupTenantMessage()
            : base(Id.Random()) { }

        public string OrganizationId { get; init; } = "org_setup";

        public string? TeamId { get; init; }
    }

    [ConfigureConsumer<SetupTenantMessage>(
        channelName: "setup.payload.worker",
        noOfPerformers: 1,
        bufferSize: 1,
        visibleTimeoutSeconds: 30,
        pollIntervalMilliseconds: 100
    )]
    private sealed class SetupTenantMessageHandler : RequestHandlerAsync<SetupTenantMessage>
    {
        public override Task<SetupTenantMessage> HandleAsync(
            SetupTenantMessage command,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(command);
    }

    private sealed class TestTenantTierResolver : ITenantTierResolver
    {
        public ValueTask<OrganizationTier> ResolveTierAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(OrganizationTier.Default);
    }
}
