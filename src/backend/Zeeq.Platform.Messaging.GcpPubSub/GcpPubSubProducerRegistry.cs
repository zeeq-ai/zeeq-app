using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Builds Brighter GCP Pub/Sub publications from the Zeeq messaging catalog.
/// </summary>
/// <remarks>
/// Feature code declares one logical publisher topic. This registry expands
/// that logical declaration through the shared Zeeq route expander and emits
/// one Brighter publication per concrete Pub/Sub topic.
/// </remarks>
public sealed class GcpPubSubProducerRegistry(
    MessagingCatalog catalog,
    ZeeqMessagingOptions messagingOptions,
    GcpPubSubMessagingOptions pubSubOptions
)
{
    /// <summary>
    /// Creates Brighter GCP Pub/Sub publication metadata.
    /// </summary>
    /// <returns>Generated publications.</returns>
    public IReadOnlyList<GcpPublication> CreatePublications()
    {
        var expander = new MessagingRouteExpander(messagingOptions.TenantBuckets);
        var validator = new GcpPubSubResourceNameValidator();

        return
        [
            .. catalog.Publishers.SelectMany(publisher =>
                CreatePublications(publisher, expander, validator)
            ),
        ];
    }

    /// <summary>
    /// Creates publications for every concrete Pub/Sub route owned by one logical publisher.
    /// </summary>
    /// <param name="publisher">Publisher metadata discovered from Zeeq attributes.</param>
    /// <param name="expander">Shared route expander for tenant, system, and immediate routes.</param>
    /// <param name="validator">Pub/Sub resource id validator.</param>
    /// <returns>Generated Brighter publications for the publisher.</returns>
    private IEnumerable<GcpPublication> CreatePublications(
        MessagingPublisher publisher,
        MessagingRouteExpander expander,
        GcpPubSubResourceNameValidator validator
    )
    {
        // Validate before yielding so bad topic names fail during metadata
        // composition, before Brighter can attempt broker provisioning.
        foreach (var route in expander.BuildRoutes(publisher))
        {
            validator.ValidateTopic(route.RoutingKey);

            yield return new GcpPublication
            {
                Topic = new RoutingKey(route.RoutingKey),
                RequestType = publisher.MessageType,
                MakeChannels = pubSubOptions.MissingChannelPolicy.ToBrighter(),
                EnableMessageOrdering = pubSubOptions.EnableMessageOrdering,
                TopicAttributes = CreateTopicAttributes(route.RoutingKey),
            };
        }
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
}
