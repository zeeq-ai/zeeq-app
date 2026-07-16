namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Expands logical publisher topics into concrete Postgres routes.
/// </summary>
public sealed class PostgresMessagingRouteBuilder(
    TenantBucketRoutingOptions bucketOptions,
    PostgresMessagingOptions postgresOptions
)
{
    private readonly MessagingRouteExpander _routeExpander = new(bucketOptions);

    /// <summary>
    /// Builds all concrete routes for a publisher declaration.
    /// </summary>
    /// <param name="publisher">Publisher metadata.</param>
    /// <returns>Concrete Postgres routes for the publisher.</returns>
    public IReadOnlyList<PostgresMessagingRoute> BuildRoutes(MessagingPublisher publisher) =>
        [.. _routeExpander.BuildRoutes(publisher).Select(ToPostgresRoute)];

    private PostgresMessagingRoute ToPostgresRoute(MessagingRoute route) =>
        new(route.RoutingKey, GetQueueStoreTable(route), route.Tier, route.Bucket);

    private string GetQueueStoreTable(MessagingRoute route) =>
        route.Kind switch
        {
            MessagingRouteKind.Immediate => postgresOptions.QueueTables.Immediate,
            MessagingRouteKind.System => postgresOptions.QueueTables.System,
            MessagingRouteKind.Tenant when route.Tier is { } tier =>
                postgresOptions.QueueTables.GetTenantTable(tier),
            _ => throw new InvalidOperationException(
                $"Postgres route {route.RoutingKey} is missing tenant route metadata."
            ),
        };
}
