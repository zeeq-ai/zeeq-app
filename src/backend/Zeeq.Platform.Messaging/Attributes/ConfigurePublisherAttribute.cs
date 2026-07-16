namespace Zeeq.Platform.Messaging;

/// <summary>
/// Base metadata for a Zeeq publisher declaration.
/// </summary>
public abstract class ConfigurePublisherAttributeBase(
    string topic,
    int visibleTimeoutSeconds = 0,
    int bufferSize = 0
) : Attribute
{
    /// <summary>
    /// Logical topic name, for example <c>orders.created</c>.
    /// </summary>
    public string Topic { get; } = topic;

    /// <summary>
    /// Message priority marker type used for default transport settings.
    /// </summary>
    public abstract Type PriorityType { get; }

    /// <summary>
    /// Default message visibility timeout in seconds.
    /// </summary>
    /// <remarks>A value of <c>0</c> means use configured messaging defaults.</remarks>
    public int VisibleTimeoutSeconds { get; } = visibleTimeoutSeconds;

    /// <summary>
    /// Default producer/consumer buffer size for generated transport metadata.
    /// </summary>
    /// <remarks>A value of <c>0</c> means use configured messaging defaults.</remarks>
    public int BufferSize { get; } = bufferSize;
}

/// <summary>
/// Declares the logical topic for a default-priority published Zeeq message.
/// </summary>
/// <remarks>
/// The topic is transport-neutral. The Postgres adapter expands it into concrete
/// routing keys and queue tables according to tenant tier, bucket, and system
/// routing options.
/// </remarks>
/// <param name="topic">
/// Logical topic name for the message type. The topic identifies the kind of
/// work being published; transport adapters expand it into concrete routes for
/// tenant buckets or system queues.
/// </param>
/// <example>
/// <c>topic: "orders.created"</c> publishes order-created messages. For tenant
/// messages, the Postgres adapter expands that topic into routes such as
/// <c>orders.created.default.03</c>. For system messages, it expands to
/// <c>orders.created.system</c>.
/// </example>
/// <param name="visibleTimeoutSeconds">
/// Default visibility timeout for consumers of this topic, in seconds. Use
/// <c>0</c> to inherit configured defaults. Explicit values should be longer
/// than normal handler execution time to avoid duplicate processing while a
/// message is still running.
/// </param>
/// <example>
/// <c>visibleTimeoutSeconds: 30</c> allows another worker to retry the message
/// after 30 seconds if a consumer crashes. <c>visibleTimeoutSeconds: 300</c>
/// gives long-running handlers more time before the message can be redelivered.
/// </example>
/// <param name="bufferSize">
/// Default number of messages consumers should fetch per poll for this topic.
/// Use <c>0</c> to inherit configured defaults, or set a small positive value
/// to tune this topic's pull behavior.
/// </param>
/// <example>
/// <c>bufferSize: 1</c> keeps each poll small for expensive work.
/// <c>bufferSize: 10</c> can improve throughput for short, idempotent handlers
/// when enough performers are available.
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigurePublisherAttribute(
    string topic,
    int visibleTimeoutSeconds = 0,
    int bufferSize = 0
)
    : ConfigurePublisherAttribute<DefaultMessage>(
        topic,
        visibleTimeoutSeconds: visibleTimeoutSeconds,
        bufferSize: bufferSize
    );

/// <summary>
/// Declares the logical topic and approved priority marker for a published message.
/// </summary>
/// <typeparam name="TPriority">
/// Approved priority marker that influences default queue table and runtime settings.
/// </typeparam>
/// <param name="topic">
/// Logical topic name for the message type. The topic remains transport-neutral;
/// priority metadata affects defaults but does not change the feature-owned
/// topic name.
/// </param>
/// <example>
/// <c>topic: "reports.refresh"</c> names report refresh work. With
/// <c>ConfigurePublisherAttribute&lt;LowPriorityMessage&gt;</c>, the topic still
/// identifies report refresh messages, but default polling and concurrency can
/// be slower than normal-priority work.
/// </example>
/// <param name="visibleTimeoutSeconds">
/// Default visibility timeout for consumers of this priority/topic combination,
/// in seconds. Use <c>0</c> to inherit configured defaults from the priority,
/// topic override, or global settings.
/// </param>
/// <example>
/// <c>visibleTimeoutSeconds: 0</c> lets <c>PriorityMessage</c> inherit its
/// configured fast-path default. <c>visibleTimeoutSeconds: 120</c> overrides
/// that default when a priority topic still performs longer-running work.
/// </example>
/// <param name="bufferSize">
/// Default number of messages consumers should fetch per poll for this
/// priority/topic combination. Use <c>0</c> to inherit configured defaults.
/// </param>
/// <example>
/// <c>bufferSize: 0</c> keeps the priority marker's configured default.
/// <c>bufferSize: 5</c> narrows a high-priority topic's fetch batch when each
/// message is moderately expensive.
/// </example>
public class ConfigurePublisherAttribute<TPriority>(
    string topic,
    int visibleTimeoutSeconds = 0,
    int bufferSize = 0
)
    : ConfigurePublisherAttributeBase(
        topic,
        visibleTimeoutSeconds: visibleTimeoutSeconds,
        bufferSize: bufferSize
    )
    where TPriority : IMessagePriority
{
    /// <summary>
    /// Message priority marker type used for default transport settings.
    /// </summary>
    public override Type PriorityType { get; } = typeof(TPriority);
}
