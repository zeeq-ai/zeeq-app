namespace Zeeq.Platform.Messaging;

/// <summary>
/// Marker for approved Zeeq message priority classes.
/// </summary>
/// <remarks>
/// This is not a queued payload contract. Implementations are marker types used
/// by publisher metadata, for example <c>[ConfigurePublisher&lt;LowPriorityMessage&gt;]</c>,
/// to select default queue settings.
/// </remarks>
public interface IMessagePriority;
