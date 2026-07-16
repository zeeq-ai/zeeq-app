using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paramore.Brighter.MessagingGateway.GcpPubSub;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Reconciles Zeeq's desired Pub/Sub topology before Brighter validates it.
/// </summary>
/// <remarks>
/// Brighter's Pub/Sub producer factory can validate topics while producer
/// services are being registered. That means topology reconciliation must also
/// be callable from setup code before Brighter producer registration, not only
/// from hosted-service startup. The hosted-service base is retained so this type
/// can still be hosted in scenarios where all Brighter validation happens later.
/// In the current runtime path, `GcpPubSubMessagingSetupExtensions` instantiates
/// this type directly and calls `EnsureTopologyAsync` from
/// `EnsureTopologyWhenValidating`; it is not registered as the mechanism that
/// performs startup reconciliation.
///
/// This type owns the Google Pub/Sub side effects for the topology flow:
/// listing existing artifacts, planning missing artifacts through
/// `GcpPubSubTopologyPlanner`, creating topics first, and creating
/// subscriptions second. It intentionally does not decide route expansion or
/// naming; those decisions are already captured by `GcpPubSubTopologyManifest`.
///
/// `GcpPubSubTopologyServiceEmulatorTests.cs` covers this class against the
/// Testcontainers Pub/Sub emulator, including missing artifact creation,
/// managed-label propagation, and idempotent second reconciliation.
/// </remarks>
public partial class GcpPubSubTopologyService(
    GcpMessagingGatewayConnection connection,
    GcpPubSubTopologyManifest manifest,
    ILogger<GcpPubSubTopologyService> logger
) : BackgroundService
{
    /// <summary>
    /// Ensures the configured Pub/Sub topology exists.
    /// </summary>
    /// <remarks>
    /// This is the callable startup hook used before Brighter validates Pub/Sub
    /// metadata. Unexpected Google client failures are allowed to fail startup;
    /// only concurrent `AlreadyExists` races are handled by the create helpers.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token for Google Pub/Sub calls.</param>
    public async Task EnsureTopologyAsync(CancellationToken cancellationToken)
    {
        var publisher = await connection.CreatePublisherServiceApiClientAsync();
        var subscriber = await connection.CreateSubscriberServiceApiClientAsync();
        var existingTopics = await GetExistingTopicsAsync(publisher, cancellationToken);

        var existingSubscriptions = await GetExistingSubscriptionsAsync(
            subscriber,
            cancellationToken
        );

        var plan = GcpPubSubTopologyPlanner.CreatePlan(
            manifest,
            existingTopics,
            existingSubscriptions
        );

        logger.LogInformation(
            "🛠️  Provisioning GCP Pub/Sub artifacts (may take up to a minute...). DesiredTopics: {DesiredTopicCount}; ExistingTopics: {ExistingTopicCount}; TopicsToCreate: {TopicCreateCount}; DesiredSubscriptions: {DesiredSubscriptionCount}; ExistingSubscriptions: {ExistingSubscriptionCount}; SubscriptionsToCreate: {SubscriptionCreateCount}",
            manifest.Topics.Count,
            manifest.Topics.Count - plan.TopicsToCreate.Count,
            plan.TopicsToCreate.Count,
            manifest.Subscriptions.Count,
            manifest.Subscriptions.Count - plan.SubscriptionsToCreate.Count,
            plan.SubscriptionsToCreate.Count
        );

        // Create topics in parallel (default parallelism)
        await Parallel.ForEachAsync(
            plan.TopicsToCreate,
            cancellationToken,
            async (topic, token) => await CreateTopicAsync(publisher, topic, token)
        );

        // Create subscriptions in parallel (default parallelism)
        await Parallel.ForEachAsync(
            plan.SubscriptionsToCreate,
            cancellationToken,
            async (subscription, token) =>
                await CreateSubscriptionAsync(subscriber, subscription, token)
        );
    }

    /// <summary>
    /// Runs reconciliation when this type is used as a hosted service.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for startup.</param>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureTopologyAsync(cancellationToken);

        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// No-op after startup reconciliation.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GCP Pub/Sub topology service is running.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Lists existing topics for every project represented in the manifest.
    /// </summary>
    /// <remarks>
    /// The planner only needs stable identities, so this method strips Google
    /// topic resources down to project id and topic id.
    /// </remarks>
    private async Task<HashSet<GcpPubSubTopicIdentity>> GetExistingTopicsAsync(
        PublisherServiceApiClient publisher,
        CancellationToken cancellationToken
    )
    {
        var result = new HashSet<GcpPubSubTopicIdentity>();
        var projectIds = manifest.Topics.Select(topic => topic.ProjectId).Distinct();

        foreach (var projectId in projectIds)
        {
            await foreach (
                var topic in publisher.ListTopicsAsync(
                    $"projects/{projectId}",
                    pageToken: null,
                    pageSize: null,
                    CallSettings.FromCancellationToken(cancellationToken)
                )
            )
            {
                if (TryParseTopicIdentity(topic.Name, out var identity))
                {
                    result.Add(identity);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Lists existing subscriptions for every project represented in the manifest.
    /// </summary>
    /// <remarks>
    /// The planner only needs stable identities, so this method strips Google
    /// subscription resources down to project id and subscription id.
    /// </remarks>
    private async Task<HashSet<GcpPubSubSubscriptionIdentity>> GetExistingSubscriptionsAsync(
        SubscriberServiceApiClient subscriber,
        CancellationToken cancellationToken
    )
    {
        var result = new HashSet<GcpPubSubSubscriptionIdentity>();
        var projectIds = manifest
            .Subscriptions.Select(subscription => subscription.ProjectId)
            .Distinct();

        foreach (var projectId in projectIds)
        {
            await foreach (
                var subscription in subscriber.ListSubscriptionsAsync(
                    $"projects/{projectId}",
                    pageToken: null,
                    pageSize: null,
                    CallSettings.FromCancellationToken(cancellationToken)
                )
            )
            {
                if (TryParseSubscriptionIdentity(subscription.Name, out var identity))
                {
                    result.Add(identity);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Creates one missing topic.
    /// </summary>
    /// <remarks>
    /// `AlreadyExists` is treated as success because multiple app instances can
    /// reconcile the same topology during rollout.
    /// </remarks>
    private async ValueTask CreateTopicAsync(
        PublisherServiceApiClient publisher,
        GcpPubSubTopicTopology topic,
        CancellationToken cancellationToken
    )
    {
        var gcpTopic = new Topic
        {
            TopicName = TopicName.FromProjectTopic(topic.ProjectId, topic.TopicId),
        };

        foreach (var (key, value) in topic.Labels)
        {
            gcpTopic.Labels[key] = value;
        }

        try
        {
            await publisher.CreateTopicAsync(
                gcpTopic,
                CallSettings.FromCancellationToken(cancellationToken)
            );

            logger.LogInformation(
                "Created GCP Pub/Sub topic {ProjectId}/{TopicId}.",
                topic.ProjectId,
                topic.TopicId
            );
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.AlreadyExists)
        {
            logger.LogInformation(
                "GCP Pub/Sub topic {ProjectId}/{TopicId} already exists.",
                topic.ProjectId,
                topic.TopicId
            );
        }
    }

    /// <summary>
    /// Creates one missing subscription.
    /// </summary>
    /// <remarks>
    /// Topics are created before subscriptions in `EnsureTopologyAsync`, so this
    /// method assumes the referenced topic already exists or is being created by
    /// another concurrent reconciler.
    /// </remarks>
    private async ValueTask CreateSubscriptionAsync(
        SubscriberServiceApiClient subscriber,
        GcpPubSubSubscriptionTopology subscription,
        CancellationToken cancellationToken
    )
    {
        var gcpSubscription = new Google.Cloud.PubSub.V1.Subscription
        {
            SubscriptionName = SubscriptionName.FromProjectSubscription(
                subscription.ProjectId,
                subscription.SubscriptionId
            ),
            TopicAsTopicName = TopicName.FromProjectTopic(
                subscription.TopicProjectId,
                subscription.TopicId
            ),
            AckDeadlineSeconds = subscription.AckDeadlineSeconds,
            RetainAckedMessages = subscription.RetainAckedMessages,
            EnableMessageOrdering = subscription.EnableMessageOrdering,
            EnableExactlyOnceDelivery = subscription.EnableExactlyOnceDelivery,
            State = Google.Cloud.PubSub.V1.Subscription.Types.State.Active,
        };

        foreach (var (key, value) in subscription.Labels)
        {
            gcpSubscription.Labels[key] = value;
        }

        if (subscription.MessageRetentionDuration is not null)
        {
            gcpSubscription.MessageRetentionDuration = Duration.FromTimeSpan(
                subscription.MessageRetentionDuration.Value
            );
        }

        try
        {
            await subscriber.CreateSubscriptionAsync(
                gcpSubscription,
                CallSettings.FromCancellationToken(cancellationToken)
            );
            logger.LogInformation(
                "Created GCP Pub/Sub subscription {ProjectId}/{SubscriptionId} for topic {TopicProjectId}/{TopicId}.",
                subscription.ProjectId,
                subscription.SubscriptionId,
                subscription.TopicProjectId,
                subscription.TopicId
            );
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.AlreadyExists)
        {
            logger.LogInformation(
                "GCP Pub/Sub subscription {ProjectId}/{SubscriptionId} already exists.",
                subscription.ProjectId,
                subscription.SubscriptionId
            );
        }
    }

    /// <summary>
    /// Parses a Google topic resource name into a stable topic identity.
    /// </summary>
    /// <remarks>
    /// Expected format: `projects/{projectId}/topics/{topicId}`.
    /// </remarks>
    private static bool TryParseTopicIdentity(
        string resourceName,
        out GcpPubSubTopicIdentity identity
    )
    {
        var parts = resourceName.Split('/');

        if (parts is ["projects", var projectId, "topics", var topicId])
        {
            identity = new(projectId, topicId);
            return true;
        }

        identity = default;

        return false;
    }

    /// <summary>
    /// Parses a Google subscription resource name into a stable subscription identity.
    /// </summary>
    /// <remarks>
    /// Expected format: `projects/{projectId}/subscriptions/{subscriptionId}`.
    /// </remarks>
    private static bool TryParseSubscriptionIdentity(
        string resourceName,
        out GcpPubSubSubscriptionIdentity identity
    )
    {
        var parts = resourceName.Split('/');

        if (parts is ["projects", var projectId, "subscriptions", var subscriptionId])
        {
            identity = new(projectId, subscriptionId);
            return true;
        }

        identity = default;

        return false;
    }
}
