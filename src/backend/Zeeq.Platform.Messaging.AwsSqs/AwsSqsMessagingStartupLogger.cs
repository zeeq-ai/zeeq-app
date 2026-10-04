using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Reports process roles and route counts when SQS messaging starts.</summary>
internal sealed partial class AwsSqsMessagingStartupLogger(
    AwsSqsMessagingStartupSnapshot registration,
    AwsSqsMessagingOptions options,
    ZeeqMessagingOptions messagingOptions,
    ILogger<AwsSqsMessagingStartupLogger> logger
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogConfigured(
            registration.Producers,
            registration.Consumers,
            options.Region,
            options.QueuePrefix,
            options.ServiceUrl is not null,
            options.MissingChannelPolicy.ToString(),
            options.LongPollSeconds,
            registration.Publications,
            registration.Subscriptions,
            registration.Publishers,
            registration.Handlers,
            $"priority={messagingOptions.TenantBuckets.PriorityBucketCount}, default={messagingOptions.TenantBuckets.DefaultBucketCount}, low={messagingOptions.TenantBuckets.LowBucketCount}",
            registration.Assemblies
        );
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AWS SQS messaging configured. Producers: {Producers}; Consumers: {Consumers}; Region: {Region}; QueuePrefix: {QueuePrefix}; Emulator: {Emulator}; MissingChannelPolicy: {MissingChannelPolicy}; LongPollSeconds: {LongPollSeconds}; Publications: {Publications}; Subscriptions: {Subscriptions}; Publishers: {Publishers}; Handlers: {Handlers}; Buckets: {Buckets}; Assemblies: {Assemblies}"
    )]
    private partial void LogConfigured(
        bool producers,
        bool consumers,
        string region,
        string queuePrefix,
        bool emulator,
        string missingChannelPolicy,
        int longPollSeconds,
        int publications,
        int subscriptions,
        int publishers,
        int handlers,
        string buckets,
        string assemblies
    );
}

/// <summary>Captures each registration's role and route metadata for startup logging.</summary>
internal sealed record AwsSqsMessagingStartupSnapshot(
    bool Producers,
    bool Consumers,
    int Publications,
    int Subscriptions,
    int Publishers,
    int Handlers,
    string Assemblies
);
