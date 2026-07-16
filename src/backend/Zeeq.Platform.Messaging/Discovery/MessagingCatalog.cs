namespace Zeeq.Platform.Messaging;

/// <summary>
/// Immutable messaging metadata discovered from feature assemblies.
/// </summary>
/// <param name="Publishers">Discovered publisher declarations keyed by message type.</param>
/// <param name="Consumers">Discovered consumer declarations keyed by handler type.</param>
public sealed record MessagingCatalog(
    IReadOnlyList<MessagingPublisher> Publishers,
    IReadOnlyList<MessagingConsumer> Consumers
)
{
    private readonly IReadOnlyDictionary<Type, MessagingPublisher> _publishersByMessageType =
        Publishers.ToDictionary(publisher => publisher.MessageType);

    /// <summary>
    /// Empty catalog used by tests and startup paths with no messaging assemblies.
    /// </summary>
    public static MessagingCatalog Empty { get; } = new([], []);

    /// <summary>
    /// Gets the publisher metadata for a message type.
    /// </summary>
    /// <param name="messageType">Message type to look up.</param>
    /// <returns>Publisher metadata when the message is known; otherwise <see langword="null"/>.</returns>
    public MessagingPublisher? FindPublisher(Type messageType) =>
        _publishersByMessageType.GetValueOrDefault(messageType);

    /// <summary>
    /// Gets the configured logical topic for a message type.
    /// </summary>
    /// <param name="messageType">Message type to look up.</param>
    /// <returns>Logical topic declared by <see cref="ConfigurePublisherAttribute"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the message type has no publisher declaration.</exception>
    public string GetPublisherTopic(Type messageType) =>
        FindPublisher(messageType)?.Topic
        ?? throw new InvalidOperationException(
            $"No Zeeq message publisher is configured for {messageType.FullName}."
        );
}

/// <summary>
/// Publisher metadata discovered from a message type.
/// </summary>
public sealed record MessagingPublisher(
    Type MessageType,
    string Topic,
    Type PriorityType,
    int VisibleTimeoutSeconds,
    int BufferSize,
    bool IsTenantMessage,
    bool IsSystemMessage
)
{
    /// <summary>
    /// True when the publisher uses the immediate priority lane.
    /// </summary>
    public bool IsImmediateMessage => PriorityType == typeof(ImmediateMessage);
}

/// <summary>
/// Consumer metadata discovered from a handler class.
/// </summary>
public sealed record MessagingConsumer(
    Type HandlerType,
    Type MessageType,
    string ChannelName,
    int NoOfPerformers,
    int BufferSize,
    int VisibleTimeoutSeconds,
    int PollIntervalMilliseconds
);
