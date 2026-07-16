using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Desired Pub/Sub topology derived from Zeeq messaging metadata.
/// </summary>
/// <remarks>
/// This manifest is the side-effect-free boundary between Zeeq's messaging
/// conventions and Google Pub/Sub management calls. Feature code declares
/// publishers and consumers with the transport-neutral Zeeq attributes; the
/// Pub/Sub adapter turns those declarations into Brighter `GcpPublication` and
/// `GcpPubSubSubscription` descriptors. This type then normalizes those
/// descriptors into the actual topics and subscriptions Zeeq expects to exist.
///
/// The separation matters because Brighter validates Pub/Sub topics while
/// producer services are registered. A hosted service alone would run too late
/// to create missing topics before that validation. By computing the topology
/// first, setup can reconcile missing artifacts before Brighter validates them,
/// while tests can still verify route expansion, labels, and idempotency without
/// contacting Pub/Sub.
///
/// Keep this type free of Google client calls. The manifest should describe
/// what should exist, not perform the work. `GcpPubSubTopologyPlanner` compares
/// this desired state with existing broker inventory, and
/// `GcpPubSubTopologyService` owns the side effects of creating missing
/// artifacts.
///
/// `GcpPubSubRegistryTests.cs` covers manifest generation from the same
/// descriptors used by the runtime setup path, including route expansion,
/// project resolution, and managed-label propagation without contacting Pub/Sub.
/// </remarks>
public sealed record GcpPubSubTopologyManifest(
    IReadOnlyList<GcpPubSubTopicTopology> Topics,
    IReadOnlyList<GcpPubSubSubscriptionTopology> Subscriptions
)
{
    /// <summary>
    /// Creates a manifest from Brighter GCP publication and subscription descriptors.
    /// </summary>
    /// <remarks>
    /// The generated Brighter descriptors remain the source of truth for route
    /// naming and transport settings. This method intentionally reuses them
    /// instead of reimplementing route expansion, so the topology reconciler and
    /// Brighter runtime validate the same concrete topics and subscriptions.
    /// </remarks>
    /// <param name="pubSubOptions">Transport options used for project fallback.</param>
    /// <param name="publications">Generated producer descriptors.</param>
    /// <param name="subscriptions">Generated consumer descriptors.</param>
    /// <returns>Desired topic and subscription topology.</returns>
    public static GcpPubSubTopologyManifest Create(
        GcpPubSubMessagingOptions pubSubOptions,
        IReadOnlyList<GcpPublication> publications,
        IReadOnlyList<GcpPubSubSubscription> subscriptions
    )
    {
        var topics = publications
            .Select(publication => CreateTopic(pubSubOptions, publication))
            .Concat(subscriptions.Select(subscription => CreateTopic(pubSubOptions, subscription)))
            .DistinctBy(topic => topic.Identity)
            .OrderBy(topic => topic.ProjectId)
            .ThenBy(topic => topic.TopicId)
            .ToArray();

        var subscriptionTopology = subscriptions
            .Select(subscription => CreateSubscription(pubSubOptions, subscription))
            .DistinctBy(subscription => subscription.Identity)
            .OrderBy(subscription => subscription.ProjectId)
            .ThenBy(subscription => subscription.SubscriptionId)
            .ToArray();

        return new(topics, subscriptionTopology);
    }

    private static GcpPubSubTopicTopology CreateTopic(
        GcpPubSubMessagingOptions pubSubOptions,
        GcpPublication publication
    )
    {
        var topicId = publication.TopicAttributes?.Name ?? publication.Topic?.Value;
        if (string.IsNullOrWhiteSpace(topicId))
        {
            throw new InvalidOperationException("Pub/Sub publication is missing a topic name.");
        }

        return new(
            ProjectId: ResolveProjectId(pubSubOptions, publication.TopicAttributes?.ProjectId),
            TopicId: topicId,
            Labels: GcpPubSubTopologyLabels.ApplyTo(publication.TopicAttributes?.Labels)
        );
    }

    private static GcpPubSubTopicTopology CreateTopic(
        GcpPubSubMessagingOptions pubSubOptions,
        GcpPubSubSubscription subscription
    )
    {
        var topicId = subscription.TopicAttributes?.Name ?? subscription.RoutingKey.Value;

        return new(
            ProjectId: ResolveProjectId(
                pubSubOptions,
                subscription.TopicAttributes?.ProjectId ?? subscription.ProjectId
            ),
            TopicId: topicId,
            Labels: GcpPubSubTopologyLabels.ApplyTo(subscription.TopicAttributes?.Labels)
        );
    }

    private static GcpPubSubSubscriptionTopology CreateSubscription(
        GcpPubSubMessagingOptions pubSubOptions,
        GcpPubSubSubscription subscription
    )
    {
        var projectId = ResolveProjectId(pubSubOptions, subscription.ProjectId);
        var topicId = subscription.TopicAttributes?.Name ?? subscription.RoutingKey.Value;
        var topicProjectId = ResolveProjectId(
            pubSubOptions,
            subscription.TopicAttributes?.ProjectId ?? subscription.ProjectId
        );

        return new(
            ProjectId: projectId,
            SubscriptionId: subscription.Name.Value,
            TopicProjectId: topicProjectId,
            TopicId: topicId,
            AckDeadlineSeconds: subscription.AckDeadlineSeconds,
            RetainAckedMessages: subscription.RetainAckedMessages,
            MessageRetentionDuration: subscription.MessageRetentionDuration,
            EnableMessageOrdering: subscription.EnableMessageOrdering,
            EnableExactlyOnceDelivery: subscription.EnableExactlyOnceDelivery,
            Labels: GcpPubSubTopologyLabels.ApplyTo(subscription.Labels)
        );
    }

    private static string ResolveProjectId(
        GcpPubSubMessagingOptions pubSubOptions,
        string? resourceProjectId
    )
    {
        var projectId = string.IsNullOrWhiteSpace(resourceProjectId)
            ? pubSubOptions.ProjectId
            : resourceProjectId;

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException(
                "GCP Pub/Sub project id must be configured before topology reconciliation."
            );
        }

        return projectId;
    }
}

/// <summary>
/// Desired Pub/Sub topic.
/// </summary>
/// <remarks>
/// Represents the topic-level state Zeeq owns for reconciliation: project,
/// topic id, and labels. It deliberately omits publish-time concerns such as
/// message bodies, mappers, and ordering keys because those belong to Brighter's
/// producer path, not topology provisioning.
/// </remarks>
public sealed record GcpPubSubTopicTopology(
    string ProjectId,
    string TopicId,
    IReadOnlyDictionary<string, string> Labels
)
{
    /// <summary>
    /// Stable identity used for comparison and idempotency.
    /// </summary>
    public GcpPubSubTopicIdentity Identity { get; } = new(ProjectId, TopicId);
}

/// <summary>
/// Desired Pub/Sub subscription.
/// </summary>
/// <remarks>
/// Represents the subscription-level state Zeeq must provision before
/// Brighter consumers validate or open channels. These fields mirror the
/// durable Pub/Sub artifact settings that can be created ahead of runtime
/// message handling, including ack deadline, ordering, exactly-once delivery,
/// retention flags, and labels.
/// </remarks>
public sealed record GcpPubSubSubscriptionTopology(
    string ProjectId,
    string SubscriptionId,
    string TopicProjectId,
    string TopicId,
    int AckDeadlineSeconds,
    bool RetainAckedMessages,
    TimeSpan? MessageRetentionDuration,
    bool EnableMessageOrdering,
    bool EnableExactlyOnceDelivery,
    IReadOnlyDictionary<string, string> Labels
)
{
    /// <summary>
    /// Stable identity used for comparison and idempotency.
    /// </summary>
    public GcpPubSubSubscriptionIdentity Identity { get; } = new(ProjectId, SubscriptionId);
}

/// <summary>
/// Stable Pub/Sub topic identity.
/// </summary>
/// <remarks>
/// Used by the planner to compare desired topics with existing broker topics
/// without carrying mutable settings such as labels.
/// </remarks>
public readonly record struct GcpPubSubTopicIdentity(string ProjectId, string TopicId);

/// <summary>
/// Stable Pub/Sub subscription identity.
/// </summary>
/// <remarks>
/// Used by the planner to compare desired subscriptions with existing broker
/// subscriptions without carrying mutable settings such as labels or ack
/// deadline.
/// </remarks>
public readonly record struct GcpPubSubSubscriptionIdentity(
    string ProjectId,
    string SubscriptionId
);
