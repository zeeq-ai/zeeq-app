using Paramore.Brighter;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Feature-facing API for publishing durable Zeeq background work.
/// </summary>
/// <remarks>
/// Feature code depends on this abstraction instead of Brighter directly. The
/// concrete transport resolves tenant tier, bucket, table, and routing key
/// before calling Brighter.
/// </remarks>
public interface IZeeqMessagePublisher
{
    /// <summary>
    /// Publishes a message to the configured transport.
    /// </summary>
    /// <typeparam name="TMessage">Message type decorated with <see cref="ConfigurePublisherAttribute"/>.</typeparam>
    /// <param name="message">Message to publish.</param>
    /// <param name="cancellationToken">Cancellation token for the publish operation.</param>
    Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class, IRequest;

    /// <summary>
    /// Publishes a message after a transport-managed delay.
    /// </summary>
    /// <remarks>
    /// Delayed transport publishing is retained only for compatibility with
    /// older call sites. New production code should use <see cref="PublishAsync{TMessage}"/>
    /// for immediate work and explicit workflow logic for retries or capacity
    /// deferral. The Pub/Sub transport does not support positive delays.
    /// </remarks>
    [Obsolete(
        "Delayed transport publishing is deprecated. Use PublishAsync for immediate work; capacity deferral must use bounded async retry in the handler. Positive delays are not supported by the Pub/Sub transport."
    )]
    Task PublishAfterAsync<TMessage>(
        TMessage message,
        TimeSpan delay,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest;
}
