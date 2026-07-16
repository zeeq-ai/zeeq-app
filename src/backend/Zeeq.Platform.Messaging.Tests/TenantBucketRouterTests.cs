using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging.Tests;

public sealed class TenantBucketRouterTests
{
    private readonly TenantBucketRouter _router = new();

    [Test]
    public async Task ToBucket_WithSameOrganizationDifferentCasing_ReturnsSameBucket()
    {
        var first = _router.ToBucket("org_ABC", bucketCount: 8);
        var second = _router.ToBucket(" org_abc ", bucketCount: 8);

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(first).IsGreaterThanOrEqualTo(0);
        await Assert.That(first).IsLessThan(8);
    }

    [Test]
    public async Task ToRoute_WithDefaultTier_ReturnsExpectedRoutingShape()
    {
        var options = new TenantBucketRoutingOptions { DefaultBucketCount = 8 };
        var route = _router.ToRoute(
            topic: "orders.created",
            organizationId: "org_default",
            tier: OrganizationTier.Default,
            options: options
        );

        await Assert.That(route.Tier).IsEqualTo(OrganizationTier.Default);
        await Assert.That(route.BucketCount).IsEqualTo(8);
        await Assert
            .That(route.RoutingKey.ToString())
            .IsEqualTo($"orders.created.default.{route.Bucket:00}");
    }

    [Test]
    public async Task TenantRoutingKey_CanBeDeconstructedAndConvertedToString()
    {
        var routingKey = new TenantRoutingKey("orders.created", OrganizationTier.Priority, 3);

        var (topic, tier, bucket) = routingKey;
        string routingKeyValue = routingKey;

        await Assert.That(topic).IsEqualTo("orders.created");
        await Assert.That(tier).IsEqualTo(OrganizationTier.Priority);
        await Assert.That(bucket).IsEqualTo(3);
        await Assert.That(routingKeyValue).IsEqualTo("orders.created.priority.03");
    }

    [Test]
    public async Task ToBucket_WithInvalidBucketCount_Throws()
    {
        await Assert
            .That(() => _router.ToBucket("org_1", bucketCount: 0))
            .Throws<ArgumentOutOfRangeException>();
    }
}
