using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Unit tests for organization endpoint filters.
///
/// Run this class:
/// dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/OrganizationActivationFilterTests/*"
/// </summary>
public sealed class OrganizationActivationFilterTests
{
    [Test]
    public async Task RouteCookieFilter_WithMatchingOrg_CallsNext()
    {
        var filter = new RequireRouteOrganizationMatchesCookieFilter();
        var context = CreateContext(routeOrgId: "org_1", cookieOrgId: "org_1");
        var nextCalled = false;

        var result = await filter.InvokeAsync(
            context,
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("next");
            }
        );

        await Assert.That(result).IsEqualTo("next");
        await Assert.That(nextCalled).IsTrue();
    }

    [Test]
    public async Task RouteCookieFilter_WithMismatchedOrg_ReturnsForbid()
    {
        var filter = new RequireRouteOrganizationMatchesCookieFilter();
        var context = CreateContext(routeOrgId: "org_1", cookieOrgId: "org_2");
        var nextCalled = false;

        var result = await filter.InvokeAsync(
            context,
            _ =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("next");
            }
        );

        await Assert.That(result).IsTypeOf<ForbidHttpResult>();
        await Assert.That(nextCalled).IsFalse();
    }

    [Test]
    public async Task RouteCookieFilter_WithMissingRouteOrg_ReturnsBadRequest()
    {
        var filter = new RequireRouteOrganizationMatchesCookieFilter();
        var context = CreateContext(routeOrgId: null, cookieOrgId: "org_1");

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));

        await Assert.That(result).IsTypeOf<BadRequest>();
    }

    [Test]
    public async Task RouteCookieFilter_WithMissingCookieOrg_ReturnsUnauthorized()
    {
        var filter = new RequireRouteOrganizationMatchesCookieFilter();
        var context = CreateContext(routeOrgId: "org_1", cookieOrgId: null);

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));

        await Assert.That(result).IsTypeOf<UnauthorizedHttpResult>();
    }

    [Test]
    public async Task ActiveOrganizationFilter_WithActiveOrg_CallsNext()
    {
        var store = new CountingMembershipStore(
            new OrganizationActivationState("org_1", DateTimeOffset.UtcNow, null)
        );
        var filter = CreateActiveOrganizationFilter(store);
        var context = CreateContext(routeOrgId: "org_1", cookieOrgId: "org_1");

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));

        await Assert.That(result).IsEqualTo("next");
    }

    [Test]
    public async Task ActiveOrganizationFilter_WithInactiveOrg_ReturnsRedirect()
    {
        var store = new CountingMembershipStore(
            new OrganizationActivationState("org_1", null, null)
        );
        var filter = CreateActiveOrganizationFilter(store);
        var context = CreateContext(routeOrgId: "org_1", cookieOrgId: "org_1");

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));

        await Assert.That(result).IsTypeOf<RedirectHttpResult>();
        var redirect = (RedirectHttpResult)result!;
        await Assert.That(redirect.Url).IsEqualTo("https://app.zeeq.ai/web/login?inactiveOrg=true");
        await Assert.That(redirect.Permanent).IsFalse();
        await Assert.That(redirect.PreserveMethod).IsFalse();
    }

    [Test]
    public async Task ActiveOrganizationFilter_WithMissingOrg_ReturnsNotFound()
    {
        var store = new CountingMembershipStore(null);
        var filter = CreateActiveOrganizationFilter(store);
        var context = CreateContext(routeOrgId: "org_1", cookieOrgId: "org_1");

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));

        await Assert.That(result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task ActiveOrganizationFilter_RepeatedChecks_UsesHybridCache()
    {
        var store = new CountingMembershipStore(
            new OrganizationActivationState("org_1", DateTimeOffset.UtcNow, null)
        );
        var filter = CreateActiveOrganizationFilter(store);

        await filter.InvokeAsync(
            CreateContext(routeOrgId: "org_1", cookieOrgId: "org_1"),
            _ => ValueTask.FromResult<object?>("next")
        );
        await filter.InvokeAsync(
            CreateContext(routeOrgId: "org_1", cookieOrgId: "org_1"),
            _ => ValueTask.FromResult<object?>("next")
        );

        await Assert.That(store.ActivationLookupCount).IsEqualTo(1);
    }

    [Test]
    public async Task ActiveCurrentOrganizationFilter_WithInactiveCookieOrg_ReturnsRedirect()
    {
        var store = new CountingMembershipStore(
            new OrganizationActivationState("org_1", null, null)
        );
        var filter = CreateActiveCurrentOrganizationFilter(store);
        var context = CreateContext(routeOrgId: null, cookieOrgId: "org_1");

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));

        await Assert.That(result).IsTypeOf<RedirectHttpResult>();
        var redirect = (RedirectHttpResult)result!;
        await Assert.That(redirect.Url).IsEqualTo("https://app.zeeq.ai/web/login?inactiveOrg=true");
    }

    [Test]
    public async Task ActiveCurrentOrganizationFilter_WithMissingCookieOrg_ReturnsUnauthorized()
    {
        var store = new CountingMembershipStore(
            new OrganizationActivationState("org_1", DateTimeOffset.UtcNow, null)
        );
        var filter = CreateActiveCurrentOrganizationFilter(store);
        var context = CreateContext(routeOrgId: null, cookieOrgId: null);

        var result = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));

        await Assert.That(result).IsTypeOf<UnauthorizedHttpResult>();
    }

    private static RequireActiveOrganizationFilter CreateActiveOrganizationFilter(
        CountingMembershipStore store
    ) =>
        new(
            CreateHybridCache(),
            store,
            CreateAuthSettings(),
            NullLogger<RequireActiveOrganizationFilter>.Instance
        );

    private static RequireActiveCurrentOrganizationFilter CreateActiveCurrentOrganizationFilter(
        CountingMembershipStore store
    ) =>
        new(
            CreateHybridCache(),
            store,
            CreateAuthSettings(),
            NullLogger<RequireActiveCurrentOrganizationFilter>.Instance
        );

    private static HybridCache CreateHybridCache() => new TestHybridCache();

    private static AuthSettings CreateAuthSettings() =>
        new() { FrontendBaseUri = "https://app.zeeq.ai/web" };

    private static EndpointFilterInvocationContext CreateContext(
        string? routeOrgId,
        string? cookieOrgId
    )
    {
        var httpContext = new DefaultHttpContext();

        if (routeOrgId is not null)
        {
            httpContext.Request.RouteValues["orgId"] = routeOrgId;
        }

        if (cookieOrgId is not null)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(AuthClaims.OrganizationId, cookieOrgId)], "Test")
            );
        }

        return EndpointFilterInvocationContext.Create(httpContext);
    }

    private sealed class CountingMembershipStore(OrganizationActivationState? state)
        : IZeeqMembershipStore
    {
        public int ActivationLookupCount { get; private set; }

        public Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
            string orgId,
            CancellationToken ct
        )
        {
            ActivationLookupCount++;

            return Task.FromResult(state);
        }

        public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
            string[] orgIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> IsSlugAvailableAsync(
            string slug,
            string? excludeOrgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task UpdateOrganizationAsync(Organization org, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> UpdateOrganizationSameDomainOnboardingAsync(
            Organization organization,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<string?> FindUserEmailByIdAsync(string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> FindUserEmailsByIdsAsync(
            string[] userIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> IsAutoInviteSameDomainAvailableAsync(
            string domain,
            string excludeOrgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> FindAutoInviteSameDomainClaimsAsync(
            string[] domains,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> CountOrganizationsCreatedByUserAsync(
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Organization?> CreateOrganizationAsync(
            Organization organization,
            Team rootTeam,
            OrganizationMembership ownerMembership,
            TeamMembership rootTeamMembership,
            int maxCreatedOrganizations,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationMembership>> ListActiveMembershipsForUserAsync(
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationMember>> ListMembersForOrganizationAsync(
            string orgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetDefaultOrganizationAsync(
            string userId,
            string orgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task UpdateMemberRoleAsync(
            string orgId,
            string userId,
            string newRole,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string?> FindRootTeamIdForMemberAsync(
            string orgId,
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<OrganizationMembership> CreateInvitationAsync(
            OrganizationMembership invitation,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForEmailAsync(
            string email,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<
            IReadOnlyList<OrganizationMembership>
        > ListPendingInvitationsForOrganizationAsync(string orgId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> AcceptInvitationAsync(
            string membershipId,
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> DeclineInvitationAsync(string membershipId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> CancelInvitationAsync(
            string orgId,
            string membershipId,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class TestHybridCache : HybridCache
    {
        private readonly Dictionary<string, object?> _values = [];

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            if (_values.TryGetValue(key, out var value))
            {
                return (T)value!;
            }

            var created = await factory(state, cancellationToken);
            _values[key] = created;

            return created;
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            _values[key] = value;

            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(
            string key,
            CancellationToken cancellationToken = default
        )
        {
            _values.Remove(key);

            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(
            string tag,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }
}
