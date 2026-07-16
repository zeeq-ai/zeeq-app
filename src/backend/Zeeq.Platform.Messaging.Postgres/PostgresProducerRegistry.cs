using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.Postgres;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Builds Brighter Postgres publications from the Zeeq messaging catalog.
/// </summary>
public sealed class PostgresProducerRegistry(
    MessagingCatalog catalog,
    ZeeqMessagingOptions messagingOptions,
    PostgresMessagingOptions postgresOptions
)
{
    /// <summary>
    /// Creates Brighter Postgres publication metadata.
    /// </summary>
    /// <returns>Generated publications.</returns>
    public IReadOnlyList<PostgresPublication> CreatePublications()
    {
        var routeBuilder = new PostgresMessagingRouteBuilder(
            messagingOptions.TenantBuckets,
            postgresOptions
        );

        return catalog
            .Publishers.SelectMany(publisher => CreatePublications(publisher, routeBuilder))
            .ToArray();
    }

    private IEnumerable<PostgresPublication> CreatePublications(
        MessagingPublisher publisher,
        PostgresMessagingRouteBuilder routeBuilder
    )
    {
        foreach (var route in routeBuilder.BuildRoutes(publisher))
        {
            yield return new PostgresPublication
            {
                Topic = new RoutingKey(route.RoutingKey),
                RequestType = publisher.MessageType,
                SchemaName = postgresOptions.SchemaName,
                QueueStoreTable = route.QueueStoreTable,
                BinaryMessagePayload = postgresOptions.BinaryMessagePayload,
                MakeChannels = postgresOptions.MissingChannelPolicy.ToBrighter(),
            };
        }
    }
}
