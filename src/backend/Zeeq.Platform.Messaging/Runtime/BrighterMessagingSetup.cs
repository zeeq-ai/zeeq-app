using Microsoft.Extensions.DependencyInjection;
using Paramore.Brighter;
using Paramore.Brighter.Extensions;
using Paramore.Brighter.Extensions.DependencyInjection;
using Paramore.Brighter.MessageMappers;
using Paramore.Brighter.Observability;
using Polly;
using Polly.Registry;
using Polly.Retry;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Shared Brighter setup used by Zeeq messaging transports.
/// </summary>
/// <remarks>
/// Postgres and Pub/Sub have different gateway metadata, but they must use the
/// same handler lifetime, retry pipeline name, retry behavior, and default JSON
/// mapper. Keeping those choices here prevents transport drift.
/// </remarks>
public static class BrighterMessagingSetup
{
    /// <summary>
    /// Default message mapper used for sync and async Brighter registration.
    /// </summary>
    public static Type JsonMessageMapperType { get; } = typeof(JsonMessageMapper<>);

    /// <summary>
    /// Applies Zeeq's shared Brighter handler and resilience configuration.
    /// </summary>
    /// <param name="options">Brighter options being configured.</param>
    /// <param name="instrumentationOptions">Transport-specific instrumentation flags.</param>
    public static void ConfigureBrighter(
        BrighterOptions options,
        InstrumentationOptions instrumentationOptions
    )
    {
        options.HandlerLifetime = ServiceLifetime.Scoped;
        options.InstrumentationOptions = instrumentationOptions;
        options.ResiliencePipelineRegistry = CreateResiliencePipelineRegistry();
    }

    /// <summary>
    /// Creates the Polly resilience pipelines used by Brighter and Zeeq handlers.
    /// </summary>
    /// <returns>Configured resilience pipeline registry.</returns>
    public static ResiliencePipelineRegistry<string> CreateResiliencePipelineRegistry()
    {
        var registry = new ResiliencePipelineRegistry<string>().AddBrighterDefault();

        registry.TryAddBuilder(
            ZeeqMessageHandler<IRequest>.DefaultRetryPipelineName,
            (builder, _) =>
                builder.AddRetry(
                    new RetryStrategyOptions
                    {
                        Delay = TimeSpan.FromMilliseconds(100),
                        MaxRetryAttempts = 3,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                    }
                )
        );

        return registry;
    }
}
