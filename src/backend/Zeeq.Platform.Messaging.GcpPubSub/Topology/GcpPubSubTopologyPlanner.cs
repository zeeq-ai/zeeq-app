namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Computes the Pub/Sub resources that must be created from a desired manifest and existing inventory.
/// </summary>
/// <remarks>
/// This planner is the side-effect-free comparison step between desired topology
/// and broker inventory. The manifest says what Zeeq expects to exist; the
/// inventory says what Pub/Sub already has; the plan returns only the missing
/// topics and subscriptions that the reconciler should create.
///
/// Keeping this logic separate from `GcpPubSubTopologyService` lets unit tests
/// verify idempotency and missing-resource decisions without a Pub/Sub emulator
/// or real GCP project. It also narrows the side-effecting code path: Google
/// client calls are responsible for listing and creating resources, while this
/// type only compares stable identities.
///
/// `GcpPubSubRegistryTests.cs` covers this planner with partial existing
/// inventory to verify that already-known topics and subscriptions are not
/// returned for creation.
/// </remarks>
public static class GcpPubSubTopologyPlanner
{
    /// <summary>
    /// Computes missing Pub/Sub topics and subscriptions.
    /// </summary>
    /// <remarks>
    /// Comparison is identity-based on project id plus topic or subscription id.
    /// Mutable settings such as labels, ack deadline, and ordering flags are not
    /// part of this create-only plan; those are carried on the topology records
    /// for creation and can be handled by a future drift/update pass if needed.
    /// </remarks>
    /// <param name="manifest">Desired Zeeq Pub/Sub topology.</param>
    /// <param name="existingTopics">Existing topic identities.</param>
    /// <param name="existingSubscriptions">Existing subscription identities.</param>
    /// <returns>The resources that need to be created.</returns>
    public static GcpPubSubTopologyPlan CreatePlan(
        GcpPubSubTopologyManifest manifest,
        IReadOnlySet<GcpPubSubTopicIdentity> existingTopics,
        IReadOnlySet<GcpPubSubSubscriptionIdentity> existingSubscriptions
    )
    {
        var topicsToCreate = manifest
            .Topics.Where(topic => !existingTopics.Contains(topic.Identity))
            .ToArray();

        var subscriptionsToCreate = manifest
            .Subscriptions.Where(subscription =>
                !existingSubscriptions.Contains(subscription.Identity)
            )
            .ToArray();

        return new(topicsToCreate, subscriptionsToCreate);
    }
}

/// <summary>
/// Missing Pub/Sub resources that should be created.
/// </summary>
/// <remarks>
/// This is the reconciler handoff object. Topics must be created before
/// subscriptions because each subscription references a topic resource.
/// </remarks>
public sealed record GcpPubSubTopologyPlan(
    IReadOnlyList<GcpPubSubTopicTopology> TopicsToCreate,
    IReadOnlyList<GcpPubSubSubscriptionTopology> SubscriptionsToCreate
);
