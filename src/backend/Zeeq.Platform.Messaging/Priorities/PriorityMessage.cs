namespace Zeeq.Platform.Messaging;

/// <summary>
/// Marker for high-priority work that should use priority queue defaults.
/// </summary>
/// <remarks>
/// This type does not carry a message payload. It is only used as publisher
/// metadata to select high-priority queue defaults for the real message type.
/// </remarks>
public sealed class PriorityMessage : IMessagePriority;
