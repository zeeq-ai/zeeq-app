using System.Reflection;
using Zeeq.Platform.Messaging.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Paramore.Brighter;
using Paramore.Brighter.Extensions.DependencyInjection;
using Paramore.Brighter.MessagingGateway.GcpPubSub;
using Paramore.Brighter.Observability;
using Paramore.Brighter.ServiceActivator.Extensions.DependencyInjection;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Registers the Brighter GCP Pub/Sub messaging transport.
/// </summary>
/// <remarks>
/// The producer and consumer methods are intentionally transport-specific
/// siblings of the Postgres setup path. Runtime provider selection happens
/// later; this library only exposes reusable Pub/Sub registration primitives.
/// </remarks>
public static class GcpPubSubMessagingSetupExtensions
{
    private const InstrumentationOptions QueueInstrumentationOptions =
        InstrumentationOptions.RequestInformation
        | InstrumentationOptions.Messaging
        | InstrumentationOptions.Brighter;

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers producer-only GCP Pub/Sub messaging services.
        /// </summary>
        /// <param name="appDatabaseConnectionString">Application database connection string used by the temporary Postgres dead-letter sink.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="pubSubOptions">GCP Pub/Sub transport options.</param>
        /// <param name="deadLetterOptions">Postgres options for the app-owned dead-letter table.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqGcpPubSubMessageProducers(
            string appDatabaseConnectionString,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            PostgresMessagingOptions deadLetterOptions,
            params Assembly[] assemblies
        ) =>
            services.AddZeeqGcpPubSubMessageProducers(
                appDatabaseConnectionString,
                messagingOptions,
                pubSubOptions,
                deadLetterOptions,
                cancellationToken: default,
                assemblies: assemblies
            );

        /// <summary>
        /// Registers producer GCP Pub/Sub messaging services, and optionally in-process consumers.
        /// </summary>
        /// <param name="appDatabaseConnectionString">Application database connection string used by the temporary Postgres dead-letter sink.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="pubSubOptions">GCP Pub/Sub transport options.</param>
        /// <param name="deadLetterOptions">Postgres options for the app-owned dead-letter table.</param>
        /// <param name="registerConsumers">Whether to also register in-process consumer services.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqGcpPubSubMessaging(
            string appDatabaseConnectionString,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            PostgresMessagingOptions deadLetterOptions,
            bool registerConsumers,
            params Assembly[] assemblies
        ) =>
            services.AddZeeqGcpPubSubMessaging(
                appDatabaseConnectionString,
                messagingOptions,
                pubSubOptions,
                deadLetterOptions,
                registerConsumers,
                cancellationToken: default,
                assemblies: assemblies
            );

        /// <summary>
        /// Registers producer GCP Pub/Sub messaging services, and optionally in-process consumers.
        /// </summary>
        /// <param name="appDatabaseConnectionString">Application database connection string used by the temporary Postgres dead-letter sink.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="pubSubOptions">GCP Pub/Sub transport options.</param>
        /// <param name="deadLetterOptions">Postgres options for the app-owned dead-letter table.</param>
        /// <param name="registerConsumers">Whether to also register in-process consumer services.</param>
        /// <param name="cancellationToken">Startup cancellation token used by topology reconciliation.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqGcpPubSubMessaging(
            string appDatabaseConnectionString,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            PostgresMessagingOptions deadLetterOptions,
            bool registerConsumers,
            CancellationToken cancellationToken,
            params Assembly[] assemblies
        )
        {
            var catalog = BuildCatalog(assemblies);

            var gatewayConnection = GcpPubSubGatewayConnectionFactory.Create(pubSubOptions);

            RegisterPlatformServices(
                services,
                appDatabaseConnectionString,
                messagingOptions,
                pubSubOptions,
                deadLetterOptions,
                gatewayConnection,
                catalog
            );

            var producerRegistry = new GcpPubSubProducerRegistry(
                catalog,
                messagingOptions,
                pubSubOptions
            );
            var publications = producerRegistry.CreatePublications();

            services.TryAddSingleton(producerRegistry);

            IReadOnlyList<GcpPubSubSubscription> subscriptions = [];

            if (registerConsumers)
            {
                var consumerRegistry = new GcpPubSubConsumerRegistry(
                    catalog,
                    messagingOptions,
                    pubSubOptions
                );

                subscriptions = consumerRegistry.CreateSubscriptions();

                services.TryAddSingleton(consumerRegistry);
                RegisterConsumerRuntime(services);
            }

            // 👇 This combined web-mode path reconciles the full desired Pub/Sub
            // topology once, avoiding separate producer and consumer API passes.
            EnsureTopologyWhenValidating(
                gatewayConnection,
                pubSubOptions,
                publications,
                subscriptions,
                cancellationToken
            );

            RegisterStartupLogging(
                services,
                GcpPubSubMessagingRegistrationKind.Producer,
                assemblies,
                catalog,
                messagingOptions,
                pubSubOptions,
                publicationCount: publications.Count,
                subscriptionCount: 0
            );

            if (registerConsumers)
            {
                RegisterStartupLogging(
                    services,
                    GcpPubSubMessagingRegistrationKind.Consumer,
                    assemblies,
                    catalog,
                    messagingOptions,
                    pubSubOptions,
                    publicationCount: 0,
                    subscriptionCount: subscriptions.Count
                );
            }

            services
                .AddBrighter(ConfigureBrighter)
                .AddProducers(_ => new ProducersConfiguration
                {
                    ProducerRegistry = new GcpPubSubProducerRegistryFactory(
                        gatewayConnection,
                        publications
                    ).Create(),
                    InstrumentationOptions = QueueInstrumentationOptions,
                })
                .AutoFromAssemblies(
                    assemblies,
                    defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                    asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                );

            if (registerConsumers)
            {
                services
                    .AddConsumers(options =>
                    {
                        ConfigureBrighter(options);
                        options.DefaultChannelFactory = new GcpPubSubChannelFactory(
                            gatewayConnection
                        );
                        options.Subscriptions = subscriptions;
                    })
                    .AutoFromAssemblies(
                        assemblies,
                        defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                        asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                    );
            }

            return services;
        }

        /// <summary>
        /// Registers producer-only GCP Pub/Sub messaging services.
        /// </summary>
        /// <param name="appDatabaseConnectionString">Application database connection string used by the temporary Postgres dead-letter sink.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="pubSubOptions">GCP Pub/Sub transport options.</param>
        /// <param name="deadLetterOptions">Postgres options for the app-owned dead-letter table.</param>
        /// <param name="cancellationToken">Startup cancellation token used by topology reconciliation.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqGcpPubSubMessageProducers(
            string appDatabaseConnectionString,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            PostgresMessagingOptions deadLetterOptions,
            CancellationToken cancellationToken,
            params Assembly[] assemblies
        )
        {
            var catalog = BuildCatalog(assemblies);

            var gatewayConnection = GcpPubSubGatewayConnectionFactory.Create(pubSubOptions);

            RegisterPlatformServices(
                services,
                appDatabaseConnectionString,
                messagingOptions,
                pubSubOptions,
                deadLetterOptions,
                gatewayConnection,
                catalog
            );

            var producerRegistry = new GcpPubSubProducerRegistry(
                catalog,
                messagingOptions,
                pubSubOptions
            );
            var publications = producerRegistry.CreatePublications();

            services.TryAddSingleton(producerRegistry);

            // 👇 This is the call path that ensures that the desired topology is
            // present in Pub/Sub before Brighter validates the producer channels.
            EnsureTopologyWhenValidating(
                gatewayConnection,
                pubSubOptions,
                publications,
                [],
                cancellationToken
            );

            RegisterStartupLogging(
                services,
                GcpPubSubMessagingRegistrationKind.Producer,
                assemblies,
                catalog,
                messagingOptions,
                pubSubOptions,
                publicationCount: publications.Count,
                subscriptionCount: 0
            );

            services
                .AddBrighter(ConfigureBrighter)
                .AddProducers(_ => new ProducersConfiguration
                {
                    ProducerRegistry = new GcpPubSubProducerRegistryFactory(
                        gatewayConnection,
                        publications
                    ).Create(),
                    InstrumentationOptions = QueueInstrumentationOptions,
                })
                .AutoFromAssemblies(
                    assemblies,
                    defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                    asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                );

            return services;
        }

        /// <summary>
        /// Registers consumer-only GCP Pub/Sub messaging services.
        /// </summary>
        /// <param name="appDatabaseConnectionString">Application database connection string used by the temporary Postgres dead-letter sink.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="pubSubOptions">GCP Pub/Sub transport options.</param>
        /// <param name="deadLetterOptions">Postgres options for the app-owned dead-letter table.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqGcpPubSubMessageConsumers(
            string appDatabaseConnectionString,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            PostgresMessagingOptions deadLetterOptions,
            params Assembly[] assemblies
        ) =>
            services.AddZeeqGcpPubSubMessageConsumers(
                appDatabaseConnectionString,
                messagingOptions,
                pubSubOptions,
                deadLetterOptions,
                cancellationToken: default,
                assemblies: assemblies
            );

        /// <summary>
        /// Registers consumer-only GCP Pub/Sub messaging services.
        /// </summary>
        /// <param name="appDatabaseConnectionString">Application database connection string used by the temporary Postgres dead-letter sink.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="pubSubOptions">GCP Pub/Sub transport options.</param>
        /// <param name="deadLetterOptions">Postgres options for the app-owned dead-letter table.</param>
        /// <param name="cancellationToken">Startup cancellation token used by topology reconciliation.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqGcpPubSubMessageConsumers(
            string appDatabaseConnectionString,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            PostgresMessagingOptions deadLetterOptions,
            CancellationToken cancellationToken,
            params Assembly[] assemblies
        )
        {
            var catalog = BuildCatalog(assemblies);
            var gatewayConnection = GcpPubSubGatewayConnectionFactory.Create(pubSubOptions);

            RegisterPlatformServices(
                services,
                appDatabaseConnectionString,
                messagingOptions,
                pubSubOptions,
                deadLetterOptions,
                gatewayConnection,
                catalog
            );

            var consumerRegistry = new GcpPubSubConsumerRegistry(
                catalog,
                messagingOptions,
                pubSubOptions
            );

            var subscriptions = consumerRegistry.CreateSubscriptions();

            services.TryAddSingleton(consumerRegistry);

            // 👇 This is the call path that ensure that the topology is present
            // for Pub/Sub
            EnsureTopologyWhenValidating(
                gatewayConnection,
                pubSubOptions,
                [],
                subscriptions,
                cancellationToken
            );

            RegisterStartupLogging(
                services,
                GcpPubSubMessagingRegistrationKind.Consumer,
                assemblies,
                catalog,
                messagingOptions,
                pubSubOptions,
                publicationCount: 0,
                subscriptionCount: subscriptions.Count
            );
            RegisterConsumerRuntime(services);

            services
                .AddConsumers(options =>
                {
                    ConfigureBrighter(options);
                    options.DefaultChannelFactory = new GcpPubSubChannelFactory(gatewayConnection);
                    options.Subscriptions = subscriptions;
                })
                .AutoFromAssemblies(
                    assemblies,
                    defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                    asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                );

            return services;
        }

        /// <summary>
        /// Registers services shared by producer and consumer setup.
        /// </summary>
        /// <remarks>
        /// Pub/Sub still reuses the app-owned Postgres dead-letter sink during
        /// this migration. That dependency is intentionally explicit through the
        /// connection string and dead-letter options parameters.
        /// </remarks>
        private static void RegisterPlatformServices(
            IServiceCollection svc,
            string appDatabaseConnectionString,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            PostgresMessagingOptions deadLetterOptions,
            GcpMessagingGatewayConnection gatewayConnection,
            MessagingCatalog catalog
        )
        {
            svc.TryAddSingleton(messagingOptions);
            svc.TryAddSingleton(pubSubOptions);
            svc.TryAddSingleton(deadLetterOptions);
            svc.TryAddSingleton(gatewayConnection);
            svc.TryAddSingleton(catalog);
            svc.TryAddSingleton<MessagingConventionValidator>();
            svc.TryAddSingleton<TenantBucketRouter>();
            svc.TryAddScoped<ZeeqMessageRouteResolver>();
            svc.TryAddSingleton<BrighterTracer>();
            svc.TryAddSingleton<IAmABrighterTracer>(provider =>
                provider.GetRequiredService<BrighterTracer>()
            );
            svc.TryAddScoped<IZeeqMessagePublisher, GcpPubSubZeeqMessagePublisher>();
            svc.TryAddSingleton<IDeadLetterWriter>(_ => new PostgresDeadLetterWriter(
                appDatabaseConnectionString,
                deadLetterOptions
            ));
            svc.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, GcpPubSubMessagingStartupLogger>()
            );
        }

        /// <summary>
        /// Registers the hosted service that opens Brighter consumers.
        /// </summary>
        private static void RegisterConsumerRuntime(IServiceCollection svc)
        {
            svc.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IHostedService,
                    BrighterMessagingConsumerHostedService
                >()
            );
        }

        /// <summary>
        /// Scans feature assemblies and validates their messaging conventions.
        /// </summary>
        private static MessagingCatalog BuildCatalog(IReadOnlyList<Assembly> assemblies)
        {
            if (assemblies.Count == 0)
            {
                throw new ArgumentException(
                    "At least one messaging assembly must be supplied.",
                    nameof(assemblies)
                );
            }

            var catalog = new MessagingCatalogScanner().Scan([.. assemblies]);

            new MessagingConventionValidator().ValidateAndThrow(catalog);

            return catalog;
        }

        /// <summary>
        /// Ensures Pub/Sub artifacts exist before Brighter validates producer or consumer metadata.
        /// </summary>
        /// <remarks>
        /// This runs only for `Validate` mode. `Assume` intentionally avoids all
        /// management calls, and `Create` delegates provisioning to Brighter. Note
        /// that this is INTENTIONALLY blocking.
        ///
        /// TODO: Check whether SIGINT can reliably cancel this path. Service
        /// registration runs before the host exposes lifetime tokens, so the
        /// caller must provide an early startup token for cancellation to flow
        /// into Pub/Sub topology reconciliation.
        /// </remarks>
        private static void EnsureTopologyWhenValidating(
            GcpMessagingGatewayConnection gatewayConnection,
            GcpPubSubMessagingOptions pubSubOptions,
            IReadOnlyList<GcpPublication> publications,
            IReadOnlyList<GcpPubSubSubscription> subscriptions,
            CancellationToken cancellationToken
        )
        {
            if (pubSubOptions.MissingChannelPolicy != GcpPubSubMissingChannelPolicy.Validate)
            {
                return;
            }

            var manifest = GcpPubSubTopologyManifest.Create(
                pubSubOptions,
                publications,
                subscriptions
            );

            var service = new GcpPubSubTopologyService(
                gatewayConnection,
                manifest,
                NullLogger<GcpPubSubTopologyService>.Instance
            );

            // 👇 This is INTENTIONALLY synchronous since we need to ensure that the
            // topology is in place before Brighter validates the Pub/Sub channels.
            service.EnsureTopologyAsync(cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Applies Brighter handler lifetime and resilience configuration.
        /// </summary>
        private static void ConfigureBrighter(BrighterOptions options)
        {
            BrighterMessagingSetup.ConfigureBrighter(options, QueueInstrumentationOptions);
        }

        /// <summary>
        /// Captures Pub/Sub registration details for startup telemetry.
        /// </summary>
        private static void RegisterStartupLogging(
            IServiceCollection svc,
            GcpPubSubMessagingRegistrationKind kind,
            IReadOnlyList<Assembly> assemblies,
            MessagingCatalog catalog,
            ZeeqMessagingOptions messagingOptions,
            GcpPubSubMessagingOptions pubSubOptions,
            int publicationCount,
            int subscriptionCount
        )
        {
            svc.AddSingleton(
                typeof(GcpPubSubMessagingStartupSnapshot),
                new GcpPubSubMessagingStartupSnapshot(
                    kind,
                    assemblies,
                    catalog,
                    messagingOptions,
                    pubSubOptions,
                    publicationCount,
                    subscriptionCount
                )
            );
        }
    }
}
