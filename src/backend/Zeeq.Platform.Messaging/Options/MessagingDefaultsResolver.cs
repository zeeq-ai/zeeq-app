namespace Zeeq.Platform.Messaging;

/// <summary>
/// Resolves layered messaging defaults for generated transport metadata.
/// </summary>
public static class MessagingDefaultsResolver
{
    private const int MinBufferSize = 1;
    private const int MaxBufferSize = 10;
    private const int MinNoOfPerformers = 1;
    private const int MaxNoOfPerformers = 32;
    private const int MinVisibleTimeoutSeconds = 1;
    private const int MaxVisibleTimeoutSeconds = 3600;
    private const int MinPollIntervalMilliseconds = 100;
    private const int MinImmediatePollIntervalMilliseconds = 50;
    private const int MaxPollIntervalMilliseconds = 60000;

    extension(ZeeqMessagingOptions options)
    {
        /// <summary>
        /// Resolves effective settings for a publisher and optional consumer declaration.
        /// </summary>
        /// <param name="publisher">Publisher metadata.</param>
        /// <param name="consumer">Optional consumer metadata.</param>
        /// <returns>Effective transport metadata defaults.</returns>
        public ResolvedMessagingDefaults ResolveDefaults(
            MessagingPublisher publisher,
            MessagingConsumer? consumer = null
        )
        {
            var minPollIntervalMilliseconds = publisher.IsImmediateMessage
                ? MinImmediatePollIntervalMilliseconds
                : MinPollIntervalMilliseconds;
            var defaults = new ResolvedMessagingDefaults(
                BufferSize: MessagingDefaultsOptions.Standard.BufferSize!.Value,
                NoOfPerformers: MessagingDefaultsOptions.Standard.NoOfPerformers!.Value,
                VisibleTimeoutSeconds: MessagingDefaultsOptions
                    .Standard
                    .VisibleTimeoutSeconds!
                    .Value,
                PollIntervalMilliseconds: MessagingDefaultsOptions
                    .Standard
                    .PollIntervalMilliseconds!
                    .Value
            );

            defaults = Apply(
                defaults,
                options.Defaults,
                MaxBufferSize,
                minPollIntervalMilliseconds
            );

            if (
                options.PriorityDefaults.TryGetValue(
                    publisher.PriorityType,
                    out var priorityDefaults
                )
            )
            {
                defaults = Apply(
                    defaults,
                    priorityDefaults,
                    MaxBufferSize,
                    minPollIntervalMilliseconds
                );
            }

            if (options.TopicOverrides.TryGetValue(publisher.Topic, out var topicDefaults))
            {
                defaults = Apply(
                    defaults,
                    topicDefaults,
                    MaxBufferSize,
                    minPollIntervalMilliseconds
                );
            }

            defaults = ApplyPublisherOverrides(
                defaults,
                publisher,
                MaxBufferSize,
                minPollIntervalMilliseconds
            );

            return consumer is null
                ? defaults
                : ApplyConsumerOverrides(
                    defaults,
                    consumer,
                    MaxBufferSize,
                    minPollIntervalMilliseconds
                );
        }
    }

    private static ResolvedMessagingDefaults Apply(
        ResolvedMessagingDefaults defaults,
        MessagingDefaultsOptions overrides,
        int maxBufferSize,
        int minPollIntervalMilliseconds
    ) =>
        defaults with
        {
            BufferSize = overrides.BufferSize is { } bufferSize
                ? ClampBufferSize(bufferSize, maxBufferSize)
                : defaults.BufferSize,
            NoOfPerformers = overrides.NoOfPerformers is { } noOfPerformers
                ? ClampNoOfPerformers(noOfPerformers)
                : defaults.NoOfPerformers,
            VisibleTimeoutSeconds = overrides.VisibleTimeoutSeconds is { } visibleTimeoutSeconds
                ? ClampVisibleTimeoutSeconds(visibleTimeoutSeconds)
                : defaults.VisibleTimeoutSeconds,
            PollIntervalMilliseconds = overrides.PollIntervalMilliseconds
                is { } pollIntervalMilliseconds
                ? ClampPollIntervalMilliseconds(
                    pollIntervalMilliseconds,
                    minPollIntervalMilliseconds
                )
                : defaults.PollIntervalMilliseconds,
        };

    private static ResolvedMessagingDefaults ApplyPublisherOverrides(
        ResolvedMessagingDefaults defaults,
        MessagingPublisher publisher,
        int maxBufferSize,
        int minPollIntervalMilliseconds
    ) =>
        defaults with
        {
            BufferSize =
                publisher.BufferSize == 0
                    ? defaults.BufferSize
                    : ClampBufferSize(publisher.BufferSize, maxBufferSize),
            VisibleTimeoutSeconds =
                publisher.VisibleTimeoutSeconds == 0
                    ? defaults.VisibleTimeoutSeconds
                    : ClampVisibleTimeoutSeconds(publisher.VisibleTimeoutSeconds),
        };

    private static ResolvedMessagingDefaults ApplyConsumerOverrides(
        ResolvedMessagingDefaults defaults,
        MessagingConsumer consumer,
        int maxBufferSize,
        int minPollIntervalMilliseconds
    ) =>
        defaults with
        {
            BufferSize =
                consumer.BufferSize == 0
                    ? defaults.BufferSize
                    : ClampBufferSize(consumer.BufferSize, maxBufferSize),
            NoOfPerformers =
                consumer.NoOfPerformers == 0
                    ? defaults.NoOfPerformers
                    : ClampNoOfPerformers(consumer.NoOfPerformers),
            VisibleTimeoutSeconds =
                consumer.VisibleTimeoutSeconds == 0
                    ? defaults.VisibleTimeoutSeconds
                    : ClampVisibleTimeoutSeconds(consumer.VisibleTimeoutSeconds),
            PollIntervalMilliseconds =
                consumer.PollIntervalMilliseconds == 0
                    ? defaults.PollIntervalMilliseconds
                    : ClampPollIntervalMilliseconds(
                        consumer.PollIntervalMilliseconds,
                        minPollIntervalMilliseconds
                    ),
        };

    private static int ClampBufferSize(int value, int maxBufferSize) =>
        Math.Clamp(value, MinBufferSize, maxBufferSize);

    private static int ClampNoOfPerformers(int value) =>
        Math.Clamp(value, MinNoOfPerformers, MaxNoOfPerformers);

    private static int ClampVisibleTimeoutSeconds(int value) =>
        Math.Clamp(value, MinVisibleTimeoutSeconds, MaxVisibleTimeoutSeconds);

    private static int ClampPollIntervalMilliseconds(int value, int minPollIntervalMilliseconds) =>
        Math.Clamp(value, minPollIntervalMilliseconds, MaxPollIntervalMilliseconds);
}
