namespace Zeeq.Platform.Messaging;

/// <summary>
/// Default concurrency, timeout, and polling settings for messaging metadata.
/// </summary>
public sealed class MessagingDefaultsOptions
{
    /// <summary>
    /// Built-in runtime defaults used before global, priority, topic, or attribute overrides.
    /// </summary>
    public static MessagingDefaultsOptions Standard { get; } =
        new()
        {
            BufferSize = 10,
            NoOfPerformers = 1,
            VisibleTimeoutSeconds = 30,
            PollIntervalMilliseconds = 1000,
        };

    /// <summary>
    /// Number of messages fetched per poll.
    /// </summary>
    public int? BufferSize { get; init; }

    /// <summary>
    /// Concurrent Brighter performers for generated subscriptions.
    /// </summary>
    public int? NoOfPerformers { get; init; }

    /// <summary>
    /// Number of seconds a fetched message remains invisible before redelivery.
    /// </summary>
    public int? VisibleTimeoutSeconds { get; init; }

    /// <summary>
    /// Delay between empty-channel polls in milliseconds.
    /// </summary>
    public int? PollIntervalMilliseconds { get; init; }
}
