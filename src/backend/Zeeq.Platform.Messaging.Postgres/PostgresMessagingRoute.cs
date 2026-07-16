using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Concrete route and table selected for a message publication or subscription.
/// </summary>
/// <param name="RoutingKey">Concrete Brighter routing key.</param>
/// <param name="QueueStoreTable">Postgres queue store table.</param>
/// <param name="Tier">Tenant tier for tenant routes, or <see langword="null"/> for system routes.</param>
/// <param name="Bucket">Bucket index for tenant routes, or <see langword="null"/> for system routes.</param>
public sealed record PostgresMessagingRoute(
    string RoutingKey,
    string QueueStoreTable,
    OrganizationTier? Tier,
    int? Bucket
);
