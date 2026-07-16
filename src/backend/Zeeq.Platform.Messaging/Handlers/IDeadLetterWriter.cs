using Paramore.Brighter;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Writes failed messages to an app-owned dead-letter sink.
/// </summary>
/// <remarks>
/// The transport implementation is responsible for preserving the original
/// Brighter message, route metadata, and failure details in a format suitable
/// for manual inspection and replay.
/// </remarks>
public interface IDeadLetterWriter
{
    /// <summary>
    /// Records a failed message after the Brighter retry pipeline falls back.
    /// </summary>
    /// <typeparam name="TMessage">Message type that failed processing.</typeparam>
    /// <param name="message">Failed message.</param>
    /// <param name="context">Brighter request context for the message pump pipeline.</param>
    /// <param name="exception">Original exception captured by Brighter's fallback policy, if available.</param>
    /// <param name="cancellationToken">Cancellation token for the dead-letter write.</param>
    Task WriteAsync<TMessage>(
        TMessage message,
        IRequestContext? context,
        Exception? exception,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest;
}
