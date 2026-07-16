using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.Postgres;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Builds Brighter Postgres subscriptions from the Zeeq messaging catalog.
/// </summary>
public sealed class PostgresConsumerRegistry(
    MessagingCatalog catalog,
    ZeeqMessagingOptions messagingOptions,
    PostgresMessagingOptions postgresOptions
)
{
    /// <summary>
    /// Creates Brighter Postgres subscription metadata.
    /// </summary>
    /// <returns>Generated subscriptions.</returns>
    public IReadOnlyList<PostgresSubscription> CreateSubscriptions()
    {
        var routeBuilder = new PostgresMessagingRouteBuilder(
            messagingOptions.TenantBuckets,
            postgresOptions
        );

        return
        [
            .. catalog.Consumers.SelectMany(consumer =>
                CreateSubscriptions(consumer, routeBuilder)
            ),
        ];
    }

    private IEnumerable<PostgresSubscription> CreateSubscriptions(
        MessagingConsumer consumer,
        PostgresMessagingRouteBuilder routeBuilder
    )
    {
        var publisher = catalog.FindPublisher(consumer.MessageType);
        if (publisher is null)
        {
            yield break;
        }

        var defaults = messagingOptions.ResolveDefaults(publisher, consumer);

        foreach (var route in routeBuilder.BuildRoutes(publisher))
        {
            yield return new PostgresSubscription(
                subscriptionName: new SubscriptionName(
                    $"{consumer.ChannelName}.{route.RoutingKey}"
                ),
                channelName: new ChannelName(route.RoutingKey),
                routingKey: new RoutingKey(route.RoutingKey),
                dataType: consumer.MessageType,
                bufferSize: defaults.BufferSize,
                noOfPerformers: defaults.NoOfPerformers,
                timeOut: TimeSpan.FromMilliseconds(defaults.PollIntervalMilliseconds),
                messagePumpType: MessagePumpType.Proactor,
                makeChannels: postgresOptions.MissingChannelPolicy.ToBrighter(),
                emptyChannelDelay: TimeSpan.FromMilliseconds(defaults.PollIntervalMilliseconds),
                schemaName: postgresOptions.SchemaName,
                queueStoreTable: route.QueueStoreTable,
                visibleTimeout: TimeSpan.FromSeconds(defaults.VisibleTimeoutSeconds),
                binaryMessagePayload: postgresOptions.BinaryMessagePayload,
                deadLetterRoutingKey: null,
                invalidMessageRoutingKey: null
            );
        }
    }
}
