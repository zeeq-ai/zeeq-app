using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Postgres-backed Zeeq message publisher.
/// </summary>
/// <remarks>
/// This wrapper selects the concrete Brighter destination before publishing so
/// the Postgres producer registry chooses the publication that owns the correct
/// queue table and routing key.
///
/// Tier lookup is intentionally delegated to <see cref="ITenantTierResolver"/>
/// instead of injecting HybridCache here. Runtime composition supplies the
/// cache-backed resolver, so publish-time routing gets cached organization tiers
/// without coupling the Postgres transport adapter to the organization data
/// model, EF Core, or the application's cache configuration.
/// </remarks>
public sealed class PostgresZeeqMessagePublisher(
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
    public async Task PublishAfterAsync<TMessage>(
        TMessage message,
        TimeSpan delay,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest
    {
        if (delay <= TimeSpan.Zero)
        {
            await PublishAsync(message, cancellationToken);
            return;
        }

        var context = await CreateRequestContextAsync(message, cancellationToken);

        await commandProcessor.PostAsync(
            delay,
            message,
            context,
            cancellationToken: cancellationToken
        );
    }

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
