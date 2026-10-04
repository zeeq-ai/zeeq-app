using System.Reflection;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Paramore.Brighter;
using Paramore.Brighter.Extensions.DependencyInjection;
using Paramore.Brighter.MessagingGateway.AWSSQS;
using Paramore.Brighter.Observability;
using Paramore.Brighter.ServiceActivator.Extensions.DependencyInjection;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Registers an isolated SQS messaging slice without application startup or database dependencies.</summary>
public static class AwsSqsMessagingSetupExtensions
{
    private const InstrumentationOptions Instrumentation =
        InstrumentationOptions.RequestInformation
        | InstrumentationOptions.Messaging
        | InstrumentationOptions.Brighter;

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers producers and optional consumers. The caller supplies ITenantTierResolver
        /// and, when consuming, IDeadLetterWriter; no in-memory production fallback is installed.
        /// </summary>
        public IServiceCollection AddZeeqAwsSqsMessaging(
            ZeeqMessagingOptions messagingOptions,
            AwsSqsMessagingOptions sqsOptions,
            AWSCredentials credentials,
            bool registerConsumers,
            params Assembly[] assemblies
        )
        {
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

            services.AddSingleton(messagingOptions);
            services.AddSingleton(sqsOptions);
            services.AddSingleton(catalog);
            services.AddSingleton(topology);
            services.AddSingleton(connection);
            services.TryAddSingleton<TenantBucketRouter>();
            services.TryAddScoped<ZeeqMessageRouteResolver>();
            services.TryAddSingleton<IAmABrighterTracer, BrighterTracer>();
            services.AddScoped<IZeeqMessagePublisher, AwsSqsZeeqMessagePublisher>();

            services
                .AddBrighter(options =>
                    BrighterMessagingSetup.ConfigureBrighter(options, Instrumentation)
                )
                .AddProducers(_ => new ProducersConfiguration
                {
                    ProducerRegistry = new SqsProducerRegistryFactory(
                        connection,
                        topology.Publications
                    ).Create(),
                    InstrumentationOptions = Instrumentation,
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
                        BrighterMessagingSetup.ConfigureBrighter(options, Instrumentation);
                        options.DefaultChannelFactory = new ChannelFactory(connection);
                        options.Subscriptions = topology.Subscriptions;
                    })
                    .AutoFromAssemblies(
                        assemblies,
                        defaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType,
                        asyncDefaultMessageMapper: BrighterMessagingSetup.JsonMessageMapperType
                    );
                services.TryAddEnumerable(
                    // Shared Zeeq hosted service owns the dispatcher; Brighter creates scoped
                    // handler pipelines using the configured HandlerLifetime, not this singleton.
                    ServiceDescriptor.Singleton<
                        IHostedService,
                        BrighterMessagingConsumerHostedService
                    >()
                );
            }

            return services;
        }
    }
}
