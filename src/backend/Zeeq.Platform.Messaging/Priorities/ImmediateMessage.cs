namespace Zeeq.Platform.Messaging;

/// <summary>
/// Marker priority for tiny user-visible acknowledgement work.
/// </summary>
/// <remarks>
/// Immediate work still carries tenant identity on the message payload, but the
/// Postgres transport routes it to one shared immediate queue lane instead of
/// spreading it across tenant tier buckets. Use this for short I/O-bound tasks
/// such as initial GitHub response comments and lightweight reactions.
/// </remarks>
public sealed class ImmediateMessage : IMessagePriority;
