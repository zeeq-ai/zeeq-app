using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Publishes through Brighter after selecting the concrete Zeeq system, immediate or tenant route.</summary>
public sealed class AwsSqsZeeqMessagePublisher(
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
        var route = await routeResolver.ResolveRouteAsync(message, cancellationToken);
        var context = new RequestContext
        {
            Destination = new ProducerKey(new RoutingKey(route.RoutingKey), CloudEventsType.Empty),
        };

        await commandProcessor.PostAsync(message, context, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    [Obsolete(
        "Delayed transport publishing is deprecated. Use PublishAsync and bounded retry in the handler."
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
            "Delayed Zeeq publishing is not implemented by this SQS prototype. Use PublishAsync and bounded retry in the handler."
        );
    }
}
