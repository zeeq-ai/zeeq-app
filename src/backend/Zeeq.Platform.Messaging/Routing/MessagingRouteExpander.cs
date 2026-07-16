using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Expands logical publisher topics into concrete transport-neutral routes.
/// </summary>
/// <remarks>
/// Feature assemblies declare one logical topic. This expander owns the shared
/// Zeeq route contract for immediate, system, and tenant bucket routes before
/// individual transports map those routes to physical resources.
/// </remarks>
public sealed class MessagingRouteExpander(TenantBucketRoutingOptions bucketOptions)
{
    /// <summary>
    /// Builds every concrete route for a publisher declaration.
    /// </summary>
    /// <param name="publisher">Publisher metadata discovered from Zeeq attributes.</param>
    /// <returns>Concrete transport-neutral routes for the publisher.</returns>
    public IReadOnlyList<MessagingRoute> BuildRoutes(MessagingPublisher publisher)
    {
        if (publisher.IsImmediateMessage)
        {
            return
            [
                new MessagingRoute(
                    $"{publisher.Topic}.immediate",
                    MessagingRouteKind.Immediate,
                    Tier: null,
                    Bucket: null
                ),
            ];
        }

        if (publisher.IsSystemMessage)
        {
            return
            [
                new MessagingRoute(
                    $"{publisher.Topic}.system",
                    MessagingRouteKind.System,
                    Tier: null,
                    Bucket: null
                ),
            ];
        }

        return
        [
            .. BuildTenantRoutes(
                publisher.Topic,
                OrganizationTier.Priority,
                bucketOptions.PriorityBucketCount
            ),
            .. BuildTenantRoutes(
                publisher.Topic,
                OrganizationTier.Default,
                bucketOptions.DefaultBucketCount
            ),
            .. BuildTenantRoutes(
                publisher.Topic,
                OrganizationTier.Low,
                bucketOptions.LowBucketCount
            ),
        ];
    }

    private static IEnumerable<MessagingRoute> BuildTenantRoutes(
        string topic,
        OrganizationTier tier,
        int bucketCount
    )
    {
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var routingKey = new TenantRoutingKey(topic, tier, bucket);

            yield return new MessagingRoute(routingKey, MessagingRouteKind.Tenant, tier, bucket);
        }
    }
}
