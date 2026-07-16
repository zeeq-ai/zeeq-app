using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Concrete tenant route selected for a published message.
/// </summary>
/// <remarks>
/// Tenant routes are split by organization tier and then by a stable bucket
/// derived from the organization identifier. The tier controls the service
/// class, such as priority, default, or low-capacity processing. The bucket
/// spreads organizations within that tier across multiple queues so one noisy
/// tenant is less likely to monopolize all workers assigned to the tier.
///
/// The selected bucket is stable for the same organization and bucket count, so
/// related work from one organization keeps a predictable route while unrelated
/// organizations can be processed in parallel on other bucket routes. Transport
/// adapters use <see cref="RoutingKey"/> as the concrete Brighter route, for
/// example <c>reports.refresh.default.03</c>, and use <see cref="Tier"/> to map
/// the route to the correct queue table or subscription group.
///
/// Changing bucket counts changes the hash modulo and can move organizations to
/// new concrete routes. Treat that as a queue migration: old bucket routes may
/// still contain messages and need to be drained or supported until empty.
/// </remarks>
/// <example>
/// Tenant routing keeps the feature-owned topic stable, then appends the
/// resolved tenant tier and stable bucket to choose the concrete queue route.
/// <code>
/// Input message:
///   Topic:          reports.refresh
///   OrganizationId: org_123
///   Resolved tier:  Default
///
/// Routing:
///   org_123 + DefaultBucketCount(8)
///       -> stable hash bucket 3
///
/// Concrete route:
///   reports.refresh.default.03
///
/// Shape:
///   {topic}.{tier}.{bucket}
///      |      |       |
///      |      |       +-- stable bucket for this organization within the tier
///      |      +---------- organization service class
///      +----------------- feature-owned logical topic
/// </code>
/// </example>
/// <param name="RoutingKey">Concrete Brighter routing key.</param>
/// <param name="Tier">Organization tier used to choose the route.</param>
/// <param name="Bucket">Selected bucket index.</param>
/// <param name="BucketCount">Bucket count used when hashing.</param>
public sealed record TenantBucketRoute(
    TenantRoutingKey RoutingKey,
    OrganizationTier Tier,
    int Bucket,
    int BucketCount
);
