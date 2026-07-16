namespace Zeeq.Platform.Messaging;

/// <summary>
/// Marker for normal-priority work.
/// </summary>
/// <remarks>
/// This type does not carry a message payload. It is only used as publisher
/// metadata to select baseline queue defaults for the real message type.
/// </remarks>
public sealed class DefaultMessage : IMessagePriority;
