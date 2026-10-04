using System.Reflection;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Paramore.Brighter;
using Paramore.Brighter.Extensions.DependencyInjection;
using Paramore.Brighter.MessagingGateway.AWSSQS;
using Paramore.Brighter.Observability;
using Paramore.Brighter.ServiceActivator.Extensions.DependencyInjection;
using Paramore.Brighter.ServiceActivator.Ports.Handlers;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Registers SQS transport services independently of application infrastructure.</summary>
public static class AwsSqsMessagingSetupExtensions
{
    private const InstrumentationOptions Instrumentation =
        InstrumentationOptions.RequestInformation
        | InstrumentationOptions.Messaging
        | InstrumentationOptions.Brighter;

    extension(IServiceCollection services)
    {
        /// <summary>Registers producers and optional consumers with caller-owned tier and dead-letter services.</summary>
        public IServiceCollection AddZeeqAwsSqsMessaging(
            ZeeqMessagingOptions messagingOptions,
            AwsSqsMessagingOptions sqsOptions,
            AWSCredentials credentials,
            bool registerConsumers,
            params Assembly[] assemblies
        ) =>
            Register(
                services,
                messagingOptions,
                sqsOptions,
                credentials,
                registerProducers: true,
                registerConsumers: registerConsumers,
                cancellationToken: default,
                assemblies: assemblies
            );

        /// <summary>Registers producers and optional consumers, cancelling startup reconciliation with the supplied token.</summary>
        public IServiceCollection AddZeeqAwsSqsMessaging(
            ZeeqMessagingOptions messagingOptions,
            AwsSqsMessagingOptions sqsOptions,
            AWSCredentials credentials,
            bool registerConsumers,
            CancellationToken cancellationToken,
            params Assembly[] assemblies
        ) =>
            Register(
                services,
                messagingOptions,
                sqsOptions,
                credentials,
                registerProducers: true,
                registerConsumers: registerConsumers,
                cancellationToken: cancellationToken,
                assemblies: assemblies
            );

        /// <summary>Registers producer-only messaging; no dispatcher or consumer hosted service is installed.</summary>
        public IServiceCollection AddZeeqAwsSqsMessageProducers(
            ZeeqMessagingOptions messagingOptions,
            AwsSqsMessagingOptions sqsOptions,
            AWSCredentials credentials,
            CancellationToken cancellationToken,
            params Assembly[] assemblies
        ) =>
            Register(
                services,
                messagingOptions,
                sqsOptions,
                credentials,
                registerProducers: true,
                registerConsumers: false,
                cancellationToken: cancellationToken,
                assemblies: assemblies
            );

        /// <summary>Registers consumer-only messaging; the caller supplies IDeadLetterWriter.</summary>
        public IServiceCollection AddZeeqAwsSqsMessageConsumers(
            ZeeqMessagingOptions messagingOptions,
            AwsSqsMessagingOptions sqsOptions,
            AWSCredentials credentials,
            CancellationToken cancellationToken,
            params Assembly[] assemblies
        ) =>
            Register(
                services,
                messagingOptions,
                sqsOptions,
                credentials,
                registerProducers: false,
                registerConsumers: true,
                cancellationToken: cancellationToken,
                assemblies: assemblies
            );
    }

    private static IServiceCollection Register(
        IServiceCollection services,
        ZeeqMessagingOptions messagingOptions,
        AwsSqsMessagingOptions sqsOptions,
        AWSCredentials credentials,
        bool registerProducers,
        bool registerConsumers,
        CancellationToken cancellationToken,
        Assembly[] assemblies
    )
    {
        // A host has one transport configuration. Use the combined API for both roles;
        // rebuilding Brighter registries in the same collection would leave stale mappings.
        if (
            services.Any(descriptor =>
                descriptor.ServiceType == typeof(AwsSqsMessagingStartupSnapshot)
            )
        )
        {
            throw new InvalidOperationException(
                "SQS messaging is already registered. Use AddZeeqAwsSqsMessaging for a combined producer-consumer host."
            );
        }
        if (assemblies.Length == 0)
        {
            throw new ArgumentException(
                "At least one messaging assembly is required.",
                nameof(assemblies)
            );
        }

        var catalog = new MessagingCatalogScanner().Scan(assemblies);
        var topology = new AwsSqsMessagingTopology(catalog, messagingOptions, sqsOptions);
        var connection = AwsSqsGatewayConnectionFactory.Create(sqsOptions, credentials);
        var publications = registerProducers ? topology.Publications : [];
        var subscriptions = registerConsumers ? topology.Subscriptions : [];

        // NOTE: Like GCP, reconciliation must complete here: Brighter's producer factory
        // validates channels during this synchronous registration call. A hosted service is too late.
        // Startup cancellation still flows through every broker call and the AWS readiness delay.
        // Create delegates to Brighter. Assume adds no management calls (Brighter still resolves queue URLs).
        if (sqsOptions.MissingChannelPolicy == OnMissingChannel.Validate)
        {
            using var client = AwsSqsGatewayConnectionFactory.CreateClient(connection);
            new AwsSqsTopologyService(
                client,
                NullLogger<AwsSqsTopologyService>.Instance,
                sqsOptions
            )
                .EnsureTopologyAsync(
                    topology,
                    registerProducers,
                    registerConsumers,
                    cancellationToken
                )
                .GetAwaiter()
                .GetResult();
        }

        services.TryAddSingleton(messagingOptions);
        services.TryAddSingleton(sqsOptions);
        services.TryAddSingleton(catalog);
        services.TryAddSingleton(topology);
        services.TryAddSingleton(connection);
        services.TryAddSingleton<TenantBucketRouter>();
        services.TryAddScoped<ZeeqMessageRouteResolver>();
        services.TryAddSingleton<IAmABrighterTracer, BrighterTracer>();
        if (registerProducers)
        {
            services.TryAddScoped<IZeeqMessagePublisher, AwsSqsZeeqMessagePublisher>();
            var producerBuilder = services
                .AddBrighter(options =>
                    BrighterMessagingSetup.ConfigureBrighter(options, Instrumentation)
                )
                .AddProducers(_ => new ProducersConfiguration
                {
                    ProducerRegistry = new SqsProducerRegistryFactory(
                        connection,
                        publications
                    ).Create(),
                    InstrumentationOptions = Instrumentation,
                });
            if (registerConsumers)
            {
                // Brighter 10.6 retains the first subscriber registry. Populate it before
                // AddConsumers constructs another builder, so combined hosts dispatch handlers.
                producerBuilder.AutoFromAssemblies(
                    assemblies,
                    defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                    asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                );
            }
            else
            {
                producerBuilder
                    .MapperRegistryFromAssemblies(
                        assemblies,
                        defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                        asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                    )
                    .TransformsFromAssemblies(assemblies);
                // Brighter 10.6 scans its loaded consumer assembly even for AddBrighter.
                // These control handlers require IDispatcher and belong only to consumers.
                services.RemoveAll<ConfigurationCommandHandler>();
                services.RemoveAll<HeartbeatRequestCommandHandler>();
            }
        }
        if (registerConsumers)
        {
            services
                .AddConsumers(options =>
                {
                    BrighterMessagingSetup.ConfigureBrighter(options, Instrumentation);
                    options.DefaultChannelFactory = new ChannelFactory(connection);
                    options.Subscriptions = subscriptions;
                })
                .AutoFromAssemblies(
                    assemblies,
                    defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                    asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                );
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IHostedService,
                    BrighterMessagingConsumerHostedService
                >()
            );
        }
        services.AddSingleton(
            new AwsSqsMessagingStartupSnapshot(
                registerProducers,
                registerConsumers,
                publications.Count,
                subscriptions.Count,
                catalog.Publishers.Count,
                catalog.Consumers.Count,
                string.Join(", ", assemblies.Select(assembly => assembly.GetName().Name).Order())
            )
        );
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, AwsSqsMessagingStartupLogger>()
        );

        return services;
    }
}
