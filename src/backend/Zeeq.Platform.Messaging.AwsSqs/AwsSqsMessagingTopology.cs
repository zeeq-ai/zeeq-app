using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.AWSSQS;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Builds matching point-to-point producer and consumer metadata from a Zeeq catalog.</summary>
/// <remarks>
/// SQS queues belong to routes, not individual handlers. Multiple independent handlers for one
/// message are rejected because SQS competing consumers cannot provide Pub/Sub fanout.
/// </remarks>
public sealed class AwsSqsMessagingTopology
{
    /// <summary>Header-bag key preserving the logical route; Brighter's SQS gateway replaces Header.Topic.</summary>
    public const string LogicalRouteHeader = "zeeqLogicalRoute";

    /// <summary>Publications indexed by Zeeq's logical route.</summary>
    public IReadOnlyList<SqsPublication> Publications { get; }

    /// <summary>Async subscriptions reading the same physical queues as their publications.</summary>
    public IReadOnlyList<SqsSubscription> Subscriptions { get; }

    /// <summary>Validates all metadata before any broker calls can occur.</summary>
    public AwsSqsMessagingTopology(
        MessagingCatalog catalog,
        ZeeqMessagingOptions messagingOptions,
        AwsSqsMessagingOptions sqsOptions
    )
    {
        new MessagingConventionValidator().ValidateAndThrow(catalog, messagingOptions);
        if (sqsOptions.LongPollSeconds is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sqsOptions),
                "SQS long polling must be between 0 and 20 seconds."
            );
        }
        if (!Enum.IsDefined(sqsOptions.MissingChannelPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sqsOptions),
                "Unknown missing-channel policy."
            );
        }
        // SQS-only is intentional: current Zeeq routes have one consumer declaration each.
        // Multiple worker instances may share that queue to distribute work. If a future
        // feature needs each independent handler to receive its own copy, add SNS fanout
        // with a separate SQS queue per handler for that route before relaxing this guard.
        if (
            catalog
                .Consumers.GroupBy(consumer => consumer.MessageType)
                .Any(group => group.Count() > 1)
        )
        {
            throw new NotSupportedException(
                "The point-to-point SQS adapter requires one consumer declaration per message type; independent handler fanout needs a separate design."
            );
        }

        var publications = new List<SqsPublication>();
        var subscriptions = new List<SqsSubscription>();
        var physicalNames = new HashSet<string>(StringComparer.Ordinal);
        var validator = new AwsSqsResourceNameValidator();
        var expander = new MessagingRouteExpander(messagingOptions.TenantBuckets);

        foreach (var publisher in catalog.Publishers)
        {
            var consumer = catalog.Consumers.SingleOrDefault(candidate =>
                candidate.MessageType == publisher.MessageType
            );
            var defaults = messagingOptions.ResolveDefaults(publisher, consumer);
            // Brighter also uses the receive wait time as a minimum visibility timeout.
            // Reject that mismatch rather than letting its SqsAttributes silently reset visibility.
            if (defaults.VisibleTimeoutSeconds < sqsOptions.LongPollSeconds)
            {
                throw new ArgumentException(
                    $"Visibility for '{publisher.Topic}' must be at least the SQS long-poll duration.",
                    nameof(messagingOptions)
                );
            }

            var attributes = new SqsAttributes(
                lockTimeout: TimeSpan.FromSeconds(defaults.VisibleTimeoutSeconds),
                timeOut: TimeSpan.FromSeconds(sqsOptions.LongPollSeconds)
            );
            foreach (var route in expander.BuildRoutes(publisher))
            {
                var queueName = validator.QueueName(sqsOptions.QueuePrefix, route.RoutingKey);
                if (!physicalNames.Add(queueName))
                {
                    throw new ArgumentException(
                        $"Multiple logical routes normalize to SQS queue '{queueName}'.",
                        nameof(catalog)
                    );
                }

                publications.Add(
                    new SqsPublication
                    {
                        Topic = new RoutingKey(route.RoutingKey),
                        ChannelName = new ChannelName(queueName),
                        RequestType = publisher.MessageType,
                        MakeChannels = sqsOptions.MissingChannelPolicy,
                        QueueAttributes = attributes,
                        // Both the producer key and this header come from this same expanded route.
                        // Publication defaults also preserve the route for direct Brighter sends.
                        DefaultHeaders = new Dictionary<string, object>
                        {
                            [LogicalRouteHeader] = route.RoutingKey,
                        },
                    }
                );

                if (consumer is not null)
                {
                    subscriptions.Add(
                        new SqsSubscription(
                            subscriptionName: new SubscriptionName(
                                $"{consumer.ChannelName}.{route.RoutingKey}"
                            ),
                            channelName: new ChannelName(queueName),
                            channelType: ChannelType.PointToPoint,
                            routingKey: new RoutingKey(route.RoutingKey),
                            requestType: publisher.MessageType,
                            bufferSize: defaults.BufferSize,
                            noOfPerformers: defaults.NoOfPerformers,
                            timeOut: TimeSpan.FromSeconds(sqsOptions.LongPollSeconds),
                            emptyChannelDelay: TimeSpan.FromMilliseconds(
                                defaults.PollIntervalMilliseconds
                            ),
                            messagePumpType: MessagePumpType.Proactor,
                            queueAttributes: attributes,
                            makeChannels: sqsOptions.MissingChannelPolicy
                        )
                    );
                }
            }
        }

        Publications = publications;
        Subscriptions = subscriptions;
    }
}
