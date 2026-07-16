using Paramore.Brighter;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Resolves the concrete Zeeq route for a message being published.
/// </summary>
/// <remarks>
/// This keeps publish-time routing transport-neutral. Postgres and Pub/Sub can
/// share the same immediate, system, and tenant bucket route selection while
/// each transport maps the selected route to its own physical resources.
/// </remarks>
public sealed class ZeeqMessageRouteResolver(
    ITenantTierResolver tierResolver,
    TenantBucketRouter bucketRouter,
    MessagingCatalog catalog,
    ZeeqMessagingOptions options
)
{
    /// <summary>
    /// Resolves the concrete route for a message instance.
    /// </summary>
    /// <typeparam name="TMessage">Published message type.</typeparam>
    /// <param name="message">Message being published.</param>
    /// <param name="cancellationToken">Cancellation token for tenant tier lookup.</param>
    /// <returns>Concrete transport-neutral route.</returns>
    public async Task<MessagingRoute> ResolveRouteAsync<TMessage>(
        TMessage message,
        CancellationToken cancellationToken
    )
        where TMessage : class, IRequest
    {
        var publisher =
            catalog.FindPublisher(typeof(TMessage))
            ?? throw new InvalidOperationException(
                $"No Zeeq message publisher is configured for {typeof(TMessage).FullName}."
            );

        if (publisher.IsImmediateMessage)
        {
            return ResolveImmediateRoute(publisher, message);
        }

        return message switch
        {
            ITenantMessage tenantMessage => await ResolveTenantRouteAsync(
                publisher.Topic,
                tenantMessage,
                cancellationToken
            ),
            ISystemMessage => new MessagingRoute(
                $"{publisher.Topic}.system",
                MessagingRouteKind.System,
                Tier: null,
                Bucket: null
            ),
            _ => throw new InvalidOperationException(
                $"{typeof(TMessage).FullName} must implement {nameof(ITenantMessage)} or {nameof(ISystemMessage)}."
            ),
        };
    }

    private static MessagingRoute ResolveImmediateRoute<TMessage>(
        MessagingPublisher publisher,
        TMessage message
    )
        where TMessage : class, IRequest
    {
        if (message is not ITenantMessage)
        {
            throw new InvalidOperationException(
                $"{typeof(TMessage).FullName} uses {nameof(ImmediateMessage)} and must implement {nameof(ITenantMessage)}."
            );
        }

        return new MessagingRoute(
            $"{publisher.Topic}.immediate",
            MessagingRouteKind.Immediate,
            Tier: null,
            Bucket: null
        );
    }

    private async Task<MessagingRoute> ResolveTenantRouteAsync(
        string baseTopic,
        ITenantMessage message,
        CancellationToken cancellationToken
    )
    {
        var tier = await tierResolver.ResolveTierAsync(message.OrganizationId, cancellationToken);

        var route = bucketRouter.ToRoute(
            baseTopic,
            message.OrganizationId,
            tier,
            options.TenantBuckets
        );

        return new MessagingRoute(
            route.RoutingKey,
            MessagingRouteKind.Tenant,
            route.Tier,
            route.Bucket
        );
    }
}
