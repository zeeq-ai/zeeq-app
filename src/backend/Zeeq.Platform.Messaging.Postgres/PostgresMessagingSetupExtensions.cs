using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Paramore.Brighter;
using Paramore.Brighter.Extensions.DependencyInjection;
using Paramore.Brighter.MessagingGateway.Postgres;
using Paramore.Brighter.Observability;
using Paramore.Brighter.ServiceActivator.Extensions.DependencyInjection;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Registers the Brighter PostgreSQL messaging transport.
/// </summary>
/// <remarks>
/// The producer and consumer registration methods build Brighter metadata during
/// service composition, before the host has created DI-backed loggers. To keep
/// that startup path observable, <c>RegisterStartupLogging</c> captures the
/// discovered catalog, route counts, queue tables, and bucket settings as
/// immutable snapshots. The one-shot <see cref="PostgresMessagingStartupLogger"/>
/// hosted service reads those snapshots after the host starts and emits the
/// final configuration telemetry through Serilog.
/// </remarks>
public static class PostgresMessagingSetupExtensions
{
    private const InstrumentationOptions QueueInstrumentationOptions =
        InstrumentationOptions.RequestInformation
        | InstrumentationOptions.Messaging
        | InstrumentationOptions.DatabaseInformation
        | InstrumentationOptions.Brighter;

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers producer-only PostgreSQL messaging services.
        /// </summary>
        /// <param name="connectionString">PostgreSQL connection string for queue writes.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="postgresOptions">PostgreSQL transport options.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqPostgresMessageProducers(
            string connectionString,
            ZeeqMessagingOptions messagingOptions,
            PostgresMessagingOptions postgresOptions,
            params Assembly[] assemblies
        )
        {
            var catalog = BuildCatalog(assemblies);
            RegisterPlatformServices(
                services,
                connectionString,
                messagingOptions,
                postgresOptions,
                catalog
            );

            var gatewayConnection = CreateGatewayConnection(connectionString, postgresOptions);
            var producerRegistry = new PostgresProducerRegistry(
                catalog,
                messagingOptions,
                postgresOptions
            );
            var publications = producerRegistry.CreatePublications();

            RegisterStartupLogging(
                services,
                PostgresMessagingRegistrationKind.Producer,
                assemblies,
                catalog,
                messagingOptions,
                postgresOptions,
                publicationCount: publications.Count,
                subscriptionCount: 0
            );

            services
                .AddBrighter(ConfigureBrighter)
                .AddProducers(_ => new ProducersConfiguration
                {
                    ProducerRegistry = new PostgresProducerRegistryFactory(
                        gatewayConnection,
                        publications
                    ).Create(),
                    // Brighter injects W3C trace context into the serialized message from
                    // the producer instrumentation path. Keep body capture disabled while
                    // still preserving publish/consume span continuity across PostgreSQL.
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
        /// Registers consumer-only PostgreSQL messaging services.
        /// </summary>
        /// <param name="connectionString">PostgreSQL connection string for queue reads.</param>
        /// <param name="messagingOptions">Transport-neutral messaging options.</param>
        /// <param name="postgresOptions">PostgreSQL transport options.</param>
        /// <param name="assemblies">Assemblies containing message types, handlers, and optional mappers.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqPostgresMessageConsumers(
            string connectionString,
            ZeeqMessagingOptions messagingOptions,
            PostgresMessagingOptions postgresOptions,
            params Assembly[] assemblies
        )
        {
            var catalog = BuildCatalog(assemblies);
            RegisterPlatformServices(
                services,
                connectionString,
                messagingOptions,
                postgresOptions,
                catalog
            );

            var gatewayConnection = CreateGatewayConnection(connectionString, postgresOptions);
            var consumerRegistry = new PostgresConsumerRegistry(
                catalog,
                messagingOptions,
                postgresOptions
            );
            var subscriptions = consumerRegistry.CreateSubscriptions();

            RegisterStartupLogging(
                services,
                PostgresMessagingRegistrationKind.Consumer,
                assemblies,
                catalog,
                messagingOptions,
                postgresOptions,
                publicationCount: 0,
                subscriptionCount: subscriptions.Count
            );
            RegisterConsumerRuntime(services);

            services
                .AddConsumers(options =>
                {
                    ConfigureBrighter(options);
                    options.DefaultChannelFactory = new PostgresChannelFactory(gatewayConnection);
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
        /// Registers services shared by producers and consumers.
        /// </summary>
        /// <remarks>
        /// Both runtime modes need the same transport-neutral options, discovered
        /// messaging catalog, tenant routing helper, and dead-letter writer. The
        /// publisher is registered here as well because producer registration
        /// needs it directly and consumer-only worker mode can safely ignore it.
        /// <c>TryAdd</c> keeps the producer and consumer paths from duplicating
        /// singleton registrations when inline consumers are enabled.
        /// </remarks>
        private static void RegisterPlatformServices(
            IServiceCollection svc,
            string connectionString,
            ZeeqMessagingOptions messagingOptions,
            PostgresMessagingOptions postgresOptions,
            MessagingCatalog catalog
        )
        {
            svc.TryAddSingleton(messagingOptions);
            svc.TryAddSingleton(postgresOptions);
            svc.TryAddSingleton(catalog);
            svc.TryAddSingleton<TenantBucketRouter>();
            svc.TryAddScoped<ZeeqMessageRouteResolver>();
            svc.TryAddSingleton<BrighterTracer>();
            svc.TryAddSingleton<IAmABrighterTracer>(provider =>
                provider.GetRequiredService<BrighterTracer>()
            );
            svc.TryAddScoped<IZeeqMessagePublisher, PostgresZeeqMessagePublisher>();
            svc.TryAddSingleton<IDeadLetterWriter>(_ => new PostgresDeadLetterWriter(
                connectionString,
                postgresOptions
            ));
            svc.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, PostgresMessagingStartupLogger>()
            );
        }

        /// <summary>
        /// Registers the hosted service that opens Brighter consumers.
        /// </summary>
        /// <remarks>
        /// <c>AddConsumers</c> builds and registers the Brighter dispatcher, but
        /// the dispatcher must still be opened when the host starts. This hosted
        /// service is only added by the consumer path so producer-only web mode
        /// can publish without starting message pumps.
        /// </remarks>
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
        /// <remarks>
        /// The catalog is the bridge between Zeeq attributes and Brighter
        /// registrations. Failing validation here stops startup before Brighter
        /// creates incomplete producer or consumer metadata, which makes broken
        /// message declarations visible during application boot instead of at
        /// first publish or consume.
        /// </remarks>
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
        /// Creates the Brighter PostgreSQL gateway connection.
        /// </summary>
        /// <remarks>
        /// Keep the Brighter-specific connection construction in one place so
        /// producers and consumers use the same schema, payload, and queue-table
        /// transport options.
        /// </remarks>
        private static PostgresMessagingGatewayConnection CreateGatewayConnection(
            string connectionString,
            PostgresMessagingOptions options
        ) =>
            new PostgresMessagingConfiguration(connectionString, options).CreateGatewayConnection();

        /// <summary>
        /// Applies Brighter handler lifetime and resilience configuration.
        /// </summary>
        /// <remarks>
        /// <see cref="ZeeqMessageHandler{TMessage}"/> decorates its sealed
        /// handler entry point with Zeeq's named retry pipeline. Brighter only
        /// registers its own internal pipeline defaults automatically, so the
        /// application must add the Zeeq key before handlers are dispatched.
        /// </remarks>
        /// <param name="options">Brighter options being configured.</param>
        private static void ConfigureBrighter(BrighterOptions options)
        {
            BrighterMessagingSetup.ConfigureBrighter(options, QueueInstrumentationOptions);
        }

        /// <summary>
        /// Captures message queue registration details for startup telemetry.
        /// </summary>
        /// <remarks>
        /// Producer and consumer registration builds the Brighter publications and
        /// subscriptions before the host can resolve normal DI-backed services.
        /// This method records the catalog, route counts, transport options, and
        /// scanned assemblies as a value-type snapshot in DI. The hosted
        /// <see cref="PostgresMessagingStartupLogger"/> later reads all snapshots
        /// once at startup and writes the configuration summary to Serilog.
        /// </remarks>
        private static void RegisterStartupLogging(
            IServiceCollection svc,
            PostgresMessagingRegistrationKind kind,
            IReadOnlyList<Assembly> assemblies,
            MessagingCatalog catalog,
            ZeeqMessagingOptions messagingOptions,
            PostgresMessagingOptions postgresOptions,
            int publicationCount,
            int subscriptionCount
        )
        {
            svc.AddSingleton(
                typeof(PostgresMessagingStartupSnapshot),
                new PostgresMessagingStartupSnapshot(
                    kind,
                    assemblies,
                    catalog,
                    messagingOptions,
                    postgresOptions,
                    publicationCount,
                    subscriptionCount
                )
            );
        }
    }
}
