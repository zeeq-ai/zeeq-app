using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// GCP Pub/Sub-backed Zeeq message publisher.
/// </summary>
/// <remarks>
/// This wrapper selects the concrete Pub/Sub topic before publishing so the
/// Brighter producer registry chooses the publication that owns the resolved
/// tenant, system, or immediate route.
/// </remarks>
public sealed class GcpPubSubZeeqMessagePublisher(
    IAmACommandProcessor commandProcessor,
    ZeeqMessageRouteResolver routeResolver
) : IZeeqMessagePublisher
{
    /// <inheritdoc />
    public async Task PublishAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest
    {
        var context = await CreateRequestContextAsync(message, cancellationToken);

        await commandProcessor.PostAsync(message, context, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    [Obsolete(
        "Delayed transport publishing is deprecated. Use PublishAsync for immediate work; capacity deferral must use bounded async retry in the handler. Positive delays are not supported by the Pub/Sub transport."
    )]
    public Task PublishAfterAsync<TMessage>(
        TMessage message,
        TimeSpan delay,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest
    {
        if (delay <= TimeSpan.Zero)
        {
            return PublishAsync(message, cancellationToken);
        }

        throw new NotSupportedException(
            "Delayed publish is not supported by the Pub/Sub transport. Capacity deferral must use bounded async retry in the handler."
        );
    }

    /// <summary>
    /// Creates a Brighter request context targeting the resolved Pub/Sub topic.
    /// </summary>
    /// <param name="message">Message being published.</param>
    /// <param name="cancellationToken">Cancellation token for tenant tier lookup.</param>
    /// <typeparam name="TMessage">Message type decorated with <see cref="ConfigurePublisherAttribute"/>.</typeparam>
    /// <returns>Request context with a concrete Brighter producer destination.</returns>
    private async Task<RequestContext> CreateRequestContextAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken
    )
        where TMessage : class, IRequest
    {
        var route = await routeResolver.ResolveRouteAsync(message, cancellationToken);

        return new()
        {
            Destination = new ProducerKey(new RoutingKey(route.RoutingKey), CloudEventsType.Empty),
        };
    }
}
