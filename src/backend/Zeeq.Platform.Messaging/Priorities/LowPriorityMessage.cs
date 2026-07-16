namespace Zeeq.Platform.Messaging;

/// <summary>
/// Marker for lower-priority work that can tolerate slower polling.
/// </summary>
/// <remarks>
/// This type does not carry a message payload. It is only used as publisher
/// metadata to select low-priority queue defaults for the real message type.
/// </remarks>
public sealed class LowPriorityMessage : IMessagePriority;
