using Google.Protobuf.Collections;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Builds Brighter GCP Pub/Sub subscriptions from the Zeeq messaging catalog.
/// </summary>
public sealed class GcpPubSubConsumerRegistry(
    MessagingCatalog catalog,
    ZeeqMessagingOptions messagingOptions,
    GcpPubSubMessagingOptions pubSubOptions
)
{
    private const int MinAckDeadlineSeconds = 10;
    private const int MaxAckDeadlineSeconds = 600;

    /// <summary>
    /// Creates Brighter GCP Pub/Sub subscription metadata.
    /// </summary>
    /// <returns>Generated subscriptions.</returns>
    public IReadOnlyList<GcpPubSubSubscription> CreateSubscriptions()
    {
        var expander = new MessagingRouteExpander(messagingOptions.TenantBuckets);
        var validator = new GcpPubSubResourceNameValidator();
        var makeChannels = pubSubOptions.MissingChannelPolicy.ToBrighter();

        return
        [
            .. catalog.Consumers.SelectMany(consumer =>
                CreateSubscriptions(consumer, expander, validator, makeChannels)
            ),
        ];
    }

    private IEnumerable<GcpPubSubSubscription> CreateSubscriptions(
        MessagingConsumer consumer,
        MessagingRouteExpander expander,
        GcpPubSubResourceNameValidator validator,
        OnMissingChannel makeChannels
    )
    {
        var publisher = catalog.FindPublisher(consumer.MessageType);
        if (publisher is null)
        {
            yield break;
        }

        var defaults = messagingOptions.ResolveDefaults(publisher, consumer);

        foreach (var route in expander.BuildRoutes(publisher))
        {
            yield return BuildSubscription(consumer, route, defaults, validator, makeChannels);
        }
    }

    private GcpPubSubSubscription BuildSubscription(
        MessagingConsumer consumer,
        MessagingRoute route,
        ResolvedMessagingDefaults defaults,
        GcpPubSubResourceNameValidator validator,
        OnMissingChannel makeChannels
    )
    {
        var subscriptionId = $"{consumer.ChannelName}.{route.RoutingKey}";

        validator.ValidateTopic(route.RoutingKey);
        validator.ValidateSubscription(subscriptionId);

        var ackDeadlineSeconds = Math.Clamp(
            pubSubOptions.AckDeadlineSeconds ?? defaults.VisibleTimeoutSeconds,
            MinAckDeadlineSeconds,
            MaxAckDeadlineSeconds
        );

        return new GcpPubSubSubscription(
            subscriptionName: new SubscriptionName(subscriptionId),
            channelName: new ChannelName(subscriptionId),
            routingKey: new RoutingKey(route.RoutingKey),
            requestType: consumer.MessageType,
            bufferSize: defaults.BufferSize,
            noOfPerformers: defaults.NoOfPerformers,
            timeOut: TimeSpan.FromMilliseconds(defaults.PollIntervalMilliseconds),
            messagePumpType: MessagePumpType.Proactor,
            makeChannels: makeChannels,
            emptyChannelDelay: TimeSpan.FromMilliseconds(defaults.PollIntervalMilliseconds),
            projectId: pubSubOptions.ProjectId,
            topicAttributes: CreateTopicAttributes(route.RoutingKey),
            ackDeadlineSeconds: ackDeadlineSeconds,
            labels: ToLabels(pubSubOptions.Labels),
            enableMessageOrdering: pubSubOptions.EnableMessageOrdering,
            enableExactlyOnceDelivery: pubSubOptions.EnableExactlyOnceDelivery,
            subscriptionMode: pubSubOptions.SubscriptionMode
        );
    }

    private TopicAttributes CreateTopicAttributes(string topicName) =>
        new()
        {
            Name = topicName,
            ProjectId = string.IsNullOrWhiteSpace(pubSubOptions.ProjectId)
                ? null
                : pubSubOptions.ProjectId,
            Labels = GcpPubSubTopologyLabels.ApplyTo(),
        };

    private static MapField<string, string> ToLabels(IReadOnlyDictionary<string, string> labels) =>
        GcpPubSubTopologyLabels.ToMapField(labels);
}
