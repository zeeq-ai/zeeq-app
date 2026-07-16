using Zeeq.Core.Models;
using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.Tests;

/// <summary>
/// Unit tests for transport-neutral route expansion and publish-time route resolution.
/// </summary>
public sealed class MessagingRouteTests
{
    [Test]
    public async Task BuildRoutes_WithTenantPublisher_ExpandsRoutesAcrossTierBuckets()
    {
        var expander = new MessagingRouteExpander(
            new TenantBucketRoutingOptions
            {
                PriorityBucketCount = 1,
                DefaultBucketCount = 2,
                LowBucketCount = 1,
            }
        );

        var routes = expander.BuildRoutes(TenantPublisher());

        await Assert.That(routes).Count().IsEqualTo(4);
        await Assert
            .That(routes.Select(route => route.RoutingKey))
            .Contains("routing.proof.priority.00");
        await Assert
            .That(routes.Select(route => route.RoutingKey))
            .Contains("routing.proof.default.01");
        await Assert
            .That(routes.Select(route => route.RoutingKey))
            .Contains("routing.proof.low.00");
        await Assert.That(routes.All(route => route.Kind == MessagingRouteKind.Tenant)).IsTrue();
    }

    [Test]
    public async Task ResolveRouteAsync_WithTenantMessage_UsesTierAndStableBucket()
    {
        var options = MessagingOptions();
        var tierResolver = new TestTenantTierResolver(OrganizationTier.Low);
        var bucketRouter = new TenantBucketRouter();
        var resolver = new ZeeqMessageRouteResolver(
            tierResolver,
            bucketRouter,
            new MessagingCatalog([TenantPublisher()], []),
            options
        );
        var message = new TenantMessage("org_123", teamId: null);

        var route = await resolver.ResolveRouteAsync(message, CancellationToken.None);
        var expected = bucketRouter.ToRoute(
            "routing.proof",
            "org_123",
            OrganizationTier.Low,
            options.TenantBuckets
        );

        await Assert.That(route.Kind).IsEqualTo(MessagingRouteKind.Tenant);
        await Assert.That(route.RoutingKey).IsEqualTo(expected.RoutingKey.ToString());
        await Assert.That(route.Tier).IsEqualTo(OrganizationTier.Low);
        await Assert.That(route.Bucket).IsEqualTo(expected.Bucket);
    }

    [Test]
    public async Task ResolveRouteAsync_WithSystemMessage_UsesSystemRoute()
    {
        var resolver = new ZeeqMessageRouteResolver(
            new TestTenantTierResolver(OrganizationTier.Default),
            new TenantBucketRouter(),
            new MessagingCatalog([SystemPublisher()], []),
            MessagingOptions()
        );

        var route = await resolver.ResolveRouteAsync(new SystemMessage(), CancellationToken.None);

        await Assert.That(route.Kind).IsEqualTo(MessagingRouteKind.System);
        await Assert.That(route.RoutingKey).IsEqualTo("system.proof.system");
        await Assert.That(route.Tier).IsNull();
        await Assert.That(route.Bucket).IsNull();
    }

    [Test]
    public async Task ResolveRouteAsync_WithImmediateMessage_UsesImmediateRoute()
    {
        var resolver = new ZeeqMessageRouteResolver(
            new TestTenantTierResolver(OrganizationTier.Default),
            new TenantBucketRouter(),
            new MessagingCatalog([ImmediatePublisher()], []),
            MessagingOptions()
        );

        var route = await resolver.ResolveRouteAsync(
            new ImmediateTenantMessage("org_123", teamId: null),
            CancellationToken.None
        );

        await Assert.That(route.Kind).IsEqualTo(MessagingRouteKind.Immediate);
        await Assert.That(route.RoutingKey).IsEqualTo("immediate.proof.immediate");
        await Assert.That(route.Tier).IsNull();
        await Assert.That(route.Bucket).IsNull();
    }

    private static MessagingPublisher TenantPublisher() =>
        new(
            typeof(TenantMessage),
            "routing.proof",
            typeof(DefaultMessage),
            VisibleTimeoutSeconds: 30,
            BufferSize: 10,
            IsTenantMessage: true,
            IsSystemMessage: false
        );

    private static MessagingPublisher SystemPublisher() =>
        new(
            typeof(SystemMessage),
            "system.proof",
            typeof(DefaultMessage),
            VisibleTimeoutSeconds: 30,
            BufferSize: 10,
            IsTenantMessage: false,
            IsSystemMessage: true
        );

    private static MessagingPublisher ImmediatePublisher() =>
        new(
            typeof(ImmediateTenantMessage),
            "immediate.proof",
            typeof(ImmediateMessage),
            VisibleTimeoutSeconds: 30,
            BufferSize: 10,
            IsTenantMessage: true,
            IsSystemMessage: false
        );

    private static ZeeqMessagingOptions MessagingOptions() =>
        new()
        {
            TenantBuckets = new TenantBucketRoutingOptions
            {
                PriorityBucketCount = 1,
                DefaultBucketCount = 2,
                LowBucketCount = 3,
            },
        };

    private sealed class TestTenantTierResolver(OrganizationTier tier) : ITenantTierResolver
    {
        public ValueTask<OrganizationTier> ResolveTierAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(tier);
    }

    private sealed class TenantMessage(string organizationId, string? teamId)
        : Event(Id.Random()),
            ITenantMessage
    {
        public string OrganizationId { get; } = organizationId;

        public string? TeamId { get; } = teamId;
    }

    private sealed class ImmediateTenantMessage(string organizationId, string? teamId)
        : Event(Id.Random()),
            ITenantMessage
    {
        public string OrganizationId { get; } = organizationId;

        public string? TeamId { get; } = teamId;
    }

    private sealed class SystemMessage : Event, ISystemMessage
    {
        public SystemMessage()
            : base(Id.Random()) { }
    }
}
