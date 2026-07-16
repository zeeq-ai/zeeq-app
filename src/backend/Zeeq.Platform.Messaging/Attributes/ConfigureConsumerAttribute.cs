using Paramore.Brighter;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Base metadata for a Zeeq consumer declaration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public abstract class ConfigureConsumerAttribute(
    Type messageType,
    string channelName,
    int noOfPerformers,
    int bufferSize,
    int visibleTimeoutSeconds,
    int pollIntervalMilliseconds
) : Attribute
{
    /// <summary>
    /// Message type handled by the decorated consumer.
    /// </summary>
    public Type MessageType { get; } = messageType;

    /// <summary>
    /// Consumer channel name used by Brighter for the generated subscription.
    /// </summary>
    public string ChannelName { get; } = channelName;

    /// <summary>
    /// Number of concurrent Brighter performers for this subscription.
    /// </summary>
    public int NoOfPerformers { get; } = noOfPerformers;

    /// <summary>
    /// Number of messages fetched per poll.
    /// </summary>
    public int BufferSize { get; } = bufferSize;

    /// <summary>
    /// Number of seconds a fetched message remains invisible before redelivery.
    /// </summary>
    public int VisibleTimeoutSeconds { get; } = visibleTimeoutSeconds;

    /// <summary>
    /// Delay between empty-channel polls in milliseconds.
    /// </summary>
    public int PollIntervalMilliseconds { get; } = pollIntervalMilliseconds;
}

/// <summary>
/// Declares that a Brighter async handler consumes a Zeeq message type.
/// </summary>
/// <remarks>
/// Feature handlers place this attribute on the concrete handler class. The
/// messaging catalog uses it to generate transport-specific subscriptions
/// without requiring feature assemblies to name Postgres tables or schemas.
/// </remarks>
/// <param name="channelName">
/// Logical consumer channel name for this handler. The channel is the Brighter
/// subscription identity and should be stable for the handler's unit of work.
/// </param>
/// <example>
/// <c>channelName: "orders.created.email"</c> routes messages for the
/// <c>orders.created</c> topic to the email handler subscription. A second
/// handler such as <c>"orders.created.analytics"</c> receives its own channel
/// and can process the same published message independently.
/// </example>
/// <param name="noOfPerformers">
/// Number of concurrent Brighter performers assigned to this subscription.
/// Use <c>0</c> to inherit configured defaults, or a positive value to override
/// concurrency for this handler.
/// </param>
/// <example>
/// <c>noOfPerformers: 1</c> processes one message at a time for ordered or
/// expensive work. <c>noOfPerformers: 4</c> allows four messages from the same
/// channel to run concurrently when the handler is safe to parallelize.
/// </example>
/// <param name="bufferSize">
/// Number of messages Brighter should fetch from the channel per poll. Use
/// <c>0</c> to inherit configured defaults, or set a small positive value to
/// tune how aggressively this handler pulls work from Postgres.
/// </param>
/// <example>
/// <c>bufferSize: 1</c> fetches one message per poll, which is useful for slow
/// handlers. <c>bufferSize: 10</c> fetches larger batches and can improve
/// throughput for short handlers when paired with enough performers.
/// </example>
/// <param name="visibleTimeoutSeconds">
/// Number of seconds a fetched message stays invisible before it can be
/// redelivered. Use <c>0</c> to inherit configured defaults. Explicit values
/// should be longer than normal handler execution time.
/// </param>
/// <example>
/// <c>visibleTimeoutSeconds: 30</c> lets another worker retry the message after
/// 30 seconds if the current worker crashes or fails to acknowledge it.
/// <c>visibleTimeoutSeconds: 300</c> is more appropriate for long-running
/// handlers where duplicate processing during normal execution would be noisy.
/// </example>
/// <param name="pollIntervalMilliseconds">
/// Delay between empty-channel polls. Use <c>0</c> to inherit configured
/// defaults. Lower values reduce idle latency, while higher values reduce
/// database polling when a channel is usually empty.
/// </param>
/// <example>
/// <c>pollIntervalMilliseconds: 500</c> checks an empty channel twice per
/// second for latency-sensitive work. <c>pollIntervalMilliseconds: 5000</c>
/// checks every five seconds for low-priority or rarely scheduled work.
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigureConsumerAttribute<TMessage>(
    string channelName,
    int noOfPerformers = 0,
    int bufferSize = 0,
    int visibleTimeoutSeconds = 0,
    int pollIntervalMilliseconds = 0
)
    : ConfigureConsumerAttribute(
        typeof(TMessage),
        channelName,
        noOfPerformers,
        bufferSize,
        visibleTimeoutSeconds,
        pollIntervalMilliseconds
    )
    where TMessage : class, IRequest;
