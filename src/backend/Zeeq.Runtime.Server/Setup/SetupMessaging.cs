using System.Reflection;
using Zeeq.Platform.CodeReviews;
using Zeeq.Platform.Ingest;
using Zeeq.Platform.Membership;
using Zeeq.Platform.Messaging.GcpPubSub;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Registers Zeeq messaging services for the selected runtime mode.
/// </summary>
/// <remarks>
/// This is the runtime composition layer for messaging. It decides which
/// concrete runtime services are available, binds the transport-neutral and
/// transport-specific options, chooses the assemblies to scan for publishers and
/// consumers, and then delegates Brighter registration to the selected messaging
/// package.
///
/// `ZEEQ_RUN_MODE` chooses the process host shape. `ZEEQ_MESSAGING_ROLE`
/// chooses whether this process registers producers, consumers, or both. This
/// keeps Cloud Run web and worker services explicit while still allowing local
/// development to run both roles in one process.
/// </remarks>
internal static class MessagingExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers messaging services for the selected process role.
        /// </summary>
        /// <remarks>
        /// Producer-only mode is used by the production web service. Consumer
        /// mode exists for one-way workers. Producer-consumer mode is used by
        /// workers that handle messages and publish follow-up messages, and by
        /// local single-process development.
        /// </remarks>
        /// <param name="appSettings">Runtime application settings, including database connection strings.</param>
        /// <param name="configuration">Configuration source for messaging option sections.</param>
        /// <param name="role">The messaging role this process should register.</param>
        /// <param name="startupCancellationToken">Cancellation token used by startup-time transport provisioning.</param>
        /// <returns>The service collection.</returns>
        public IServiceCollection AddZeeqMessaging(
            AppSettings appSettings,
            IConfiguration configuration,
            ZeeqMessagingRuntimeRole role,
            CancellationToken startupCancellationToken = default
        )
        {
            AddRuntimeMessagingServices(services);

            var provider = GetProvider(configuration);
            var messagingOptions = GetMessagingOptions(configuration);
            var postgresOptions = GetPostgresOptions(configuration);
            var assemblies = GetMessagingAssemblies();
            var registerProducers =
                role
                is ZeeqMessagingRuntimeRole.Producer
                    or ZeeqMessagingRuntimeRole.ProducerConsumer;
            var registerConsumers =
                role
                is ZeeqMessagingRuntimeRole.Consumer
                    or ZeeqMessagingRuntimeRole.ProducerConsumer;

            switch (provider)
            {
                case MessagingProvider.Postgres:
                    if (registerProducers)
                    {
                        services.AddZeeqPostgresMessageProducers(
                            appSettings.Database.EffectiveConnectionString,
                            messagingOptions,
                            postgresOptions,
                            assemblies
                        );
                    }

                    if (registerConsumers)
                    {
                        services.AddZeeqPostgresMessageConsumers(
                            appSettings.Database.EffectiveConnectionString,
                            messagingOptions,
                            postgresOptions,
                            assemblies
                        );
                    }

                    break;

                case MessagingProvider.GcpPubSub:
                    var pubSubOptions = GetGcpPubSubOptions(configuration);

                    switch (role)
                    {
                        case ZeeqMessagingRuntimeRole.Producer:
                            services.AddZeeqGcpPubSubMessaging(
                                appSettings.Database.EffectiveConnectionString,
                                messagingOptions,
                                pubSubOptions,
                                postgresOptions,
                                false,
                                startupCancellationToken,
                                assemblies
                            );
                            break;

                        case ZeeqMessagingRuntimeRole.Consumer:
                            services.AddZeeqGcpPubSubMessageConsumers(
                                appSettings.Database.EffectiveConnectionString,
                                messagingOptions,
                                pubSubOptions,
                                postgresOptions,
                                startupCancellationToken,
                                assemblies
                            );
                            break;

                        case ZeeqMessagingRuntimeRole.ProducerConsumer:
                            services.AddZeeqGcpPubSubMessaging(
                                appSettings.Database.EffectiveConnectionString,
                                messagingOptions,
                                pubSubOptions,
                                postgresOptions,
                                true,
                                startupCancellationToken,
                                assemblies
                            );
                            break;
                    }

                    break;
            }

            return services;
        }
    }

    /// <summary>
    /// Adds runtime-owned services needed by the transport-neutral messaging layer.
    /// </summary>
    /// <remarks>
    /// The messaging platform depends on abstractions. Runtime composition wires
    /// those abstractions to application services such as HybridCache and the
    /// Postgres data context.
    /// </remarks>
    /// <param name="svc">Service collection to update.</param>
    private static void AddRuntimeMessagingServices(IServiceCollection svc)
    {
        svc.AddScoped<ITenantTierResolver, HybridCacheTenantTierResolver>();
    }

    /// <summary>
    /// Binds transport-neutral messaging options.
    /// </summary>
    /// <remarks>
    /// Missing configuration is valid; the messaging package supplies defaults
    /// for bucket counts, priority behavior, timeouts, buffers, and polling.
    /// </remarks>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>Bound messaging options or defaults.</returns>
    private static ZeeqMessagingOptions GetMessagingOptions(IConfiguration configuration) =>
        configuration.GetSection(ZeeqMessagingOptions.SectionName).Get<ZeeqMessagingOptions>()
        ?? new();

    /// <summary>
    /// Resolves the configured messaging transport provider.
    /// </summary>
    /// <remarks>
    /// Missing configuration deliberately resolves to Postgres. That keeps local
    /// and production behavior unchanged unless an operator explicitly opts into
    /// Pub/Sub with <c>ZeeqMessaging:Provider=GcpPubSub</c>.
    /// </remarks>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The selected messaging provider.</returns>
    private static MessagingProvider GetProvider(IConfiguration configuration)
    {
        if (RuntimeConfig.ForcePostgresMessaging)
        {
            return MessagingProvider.Postgres;
        }

        var providerName = configuration[$"{ZeeqMessagingOptions.SectionName}:Provider"];

        if (string.IsNullOrWhiteSpace(providerName))
        {
            return MessagingProvider.Postgres;
        }

        if (Enum.TryParse<MessagingProvider>(providerName, ignoreCase: true, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"Unsupported messaging provider '{providerName}'. Supported providers are Postgres and GcpPubSub."
        );
    }

    /// <summary>
    /// Binds PostgreSQL transport options for queue tables and payload behavior.
    /// </summary>
    /// <remarks>
    /// These options stay separate from <see cref="ZeeqMessagingOptions"/> so
    /// feature code and transport-neutral routing do not need to know Postgres
    /// table names.
    /// </remarks>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>Bound Postgres messaging options or defaults.</returns>
    private static PostgresMessagingOptions GetPostgresOptions(IConfiguration configuration) =>
        configuration
            .GetSection(PostgresMessagingOptions.SectionName)
            .Get<PostgresMessagingOptions>()
        ?? new();

    /// <summary>
    /// Binds Google Cloud Pub/Sub transport options.
    /// </summary>
    /// <remarks>
    /// Aspire exports <c>PUBSUB_PROJECT_ID</c> for the local emulator. Cloud
    /// Run deployment scripts pass <c>GCP_PROJECT_ID</c>, and Google libraries
    /// commonly use <c>GOOGLE_CLOUD_PROJECT</c>. Falling back to those variables
    /// keeps the runtime provider switch small while allowing operators to set
    /// the explicit <c>ZeeqMessaging:GcpPubSub:ProjectId</c> value when needed.
    /// </remarks>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>Bound Pub/Sub options with environment project-id fallback.</returns>
    private static GcpPubSubMessagingOptions GetGcpPubSubOptions(IConfiguration configuration)
    {
        var options =
            configuration
                .GetSection(GcpPubSubMessagingOptions.SectionName)
                .Get<GcpPubSubMessagingOptions>()
            ?? new();

        if (!string.IsNullOrWhiteSpace(options.ProjectId))
        {
            return options;
        }

        var projectId =
            configuration["PUBSUB_PROJECT_ID"]
            ?? configuration["GCP_PROJECT_ID"]
            ?? configuration["GOOGLE_CLOUD_PROJECT"];

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return options;
        }

        return new GcpPubSubMessagingOptions
        {
            ProjectId = projectId,
            MissingChannelPolicy = options.MissingChannelPolicy,
            SubscriptionMode = options.SubscriptionMode,
            EnableMessageOrdering = options.EnableMessageOrdering,
            EnableExactlyOnceDelivery = options.EnableExactlyOnceDelivery,
            AckDeadlineSeconds = options.AckDeadlineSeconds,
            Labels = options.Labels,
            UseEmulatorDetection = options.UseEmulatorDetection,
        };
    }

    /// <summary>
    /// Gets assemblies scanned for messaging publishers, consumers, and handlers.
    /// </summary>
    /// <remarks>
    /// Keep this list explicit so startup validates only the feature surfaces
    /// that intentionally participate in messaging. Add a feature assembly here
    /// when it introduces messages or handlers decorated with Zeeq messaging
    /// attributes.
    /// </remarks>
    /// <returns>Assemblies included in messaging catalog discovery.</returns>
    private static Assembly[] GetMessagingAssemblies() =>
        [
            Assembly.GetExecutingAssembly(),
            typeof(OrganizationEndpoints).Assembly,
            typeof(SetupCodeReviews).Assembly,
            typeof(SetupMcpExtensions).Assembly,
            typeof(SetupZeeqIngest).Assembly,
            typeof(Zeeq.Platform.Metrics.SetupZeeqMetrics).Assembly,
        ];
}

/// <summary>
/// Runtime-selectable messaging transport providers.
/// </summary>
internal enum MessagingProvider
{
    /// <summary>
    /// Use Brighter's PostgreSQL messaging gateway.
    /// </summary>
    Postgres,

    /// <summary>
    /// Use Brighter's Google Cloud Pub/Sub messaging gateway.
    /// </summary>
    GcpPubSub,
}
