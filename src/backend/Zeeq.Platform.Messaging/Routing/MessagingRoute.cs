using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Transport-neutral concrete route for a publisher declaration.
/// </summary>
/// <remarks>
/// This is the shared route model between the feature-facing catalog and
/// transport adapters. Adapters can map <see cref="RoutingKey"/> to their native
/// resource names while using <see cref="Kind"/>, <see cref="Tier"/>, and
/// <see cref="Bucket"/> for physical resource selection.
/// </remarks>
/// <param name="RoutingKey">Concrete Brighter routing key.</param>
/// <param name="Kind">Concrete route lane.</param>
/// <param name="Tier">Tenant tier for tenant routes, or <see langword="null"/> otherwise.</param>
/// <param name="Bucket">Bucket index for tenant routes, or <see langword="null"/> otherwise.</param>
public sealed record MessagingRoute(
    string RoutingKey,
    MessagingRouteKind Kind,
    OrganizationTier? Tier,
    int? Bucket
);
