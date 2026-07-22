using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Unit tests for the cached membership-activation lookup used by
/// <c>UserTokenValidationMiddleware</c>, and for cache eviction on the
/// membership mutations that can flip activation state.
///
/// Run this class:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/CachedZeeqMembershipStoreTests/*"
/// </summary>
public sealed class CachedZeeqMembershipStoreTests
{
    [Test]
    public async Task FindMembershipActivationState_CachesResult_SecondCallHitsCache()
    {
        var inner = new CountingMembershipStore(
            new MembershipActivationState("org_1", "user_1", MembershipStatus.Active, false)
        );
        var store = CreateStore(inner, new TestHybridCache());

        var first = await store.FindMembershipActivationStateAsync(
            "org_1",
            "user_1",
            CancellationToken.None
        );
        var second = await store.FindMembershipActivationStateAsync(
            "org_1",
            "user_1",
            CancellationToken.None
        );

        await Assert.That(first?.IsActive).IsTrue();
        await Assert.That(second?.IsActive).IsTrue();
        await Assert.That(inner.ActivationLookupCount).IsEqualTo(1);
    }

    [Test]
    public async Task FindMembershipActivationState_CacheOptions_UseThirtySecondL2OnlyTtl()
    {
        var inner = new CountingMembershipStore(
            new MembershipActivationState("org_1", "user_1", MembershipStatus.Active, false)
        );
        var cache = new TestHybridCache();
        var store = CreateStore(inner, cache);

        await store.FindMembershipActivationStateAsync("org_1", "user_1", CancellationToken.None);

        await Assert.That(cache.LastOptions?.Expiration).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert
            .That(cache.LastOptions?.Flags)
            .IsEqualTo(HybridCacheEntryFlags.DisableLocalCache);
    }

    [Test]
    public async Task RemoveMemberAsync_EvictsCachedActivationState()
    {
        var inner = new CountingMembershipStore(
            new MembershipActivationState("org_1", "user_1", MembershipStatus.Active, false)
        );
        var cache = new TestHybridCache();
        var store = CreateStore(inner, cache);

        var before = await store.FindMembershipActivationStateAsync(
            "org_1",
            "user_1",
            CancellationToken.None
        );
        inner.State = new MembershipActivationState(
            "org_1",
            "user_1",
            MembershipStatus.Disabled,
            true
        );
        await store.RemoveMemberAsync("org_1", "user_1", CancellationToken.None);
        var after = await store.FindMembershipActivationStateAsync(
            "org_1",
            "user_1",
            CancellationToken.None
        );

        // Guards that removal evicts the cache entry rather than leaving a
        // stale "active" result for the remainder of the 30-second TTL.
        await Assert.That(before?.IsActive).IsTrue();
        await Assert.That(after?.IsActive).IsFalse();
        await Assert.That(inner.ActivationLookupCount).IsEqualTo(2);
    }

    [Test]
    public async Task RemoveMemberAsync_CacheEvictionThrows_DoesNotFailTheMutation()
    {
        var inner = new CountingMembershipStore(
            new MembershipActivationState("org_1", "user_1", MembershipStatus.Active, false)
        );
        var cache = new ThrowingHybridCache();
        var store = CreateStore(inner, cache);

        // Guards that a broken/unavailable L2 cache cannot turn an
        // already-successful membership removal into a thrown exception —
        // eviction is best-effort, bounded by the 30-second TTL.
        Func<Task> act = () => store.RemoveMemberAsync("org_1", "user_1", CancellationToken.None);

        await Assert.That(act).ThrowsNothing();
        await Assert.That(inner.RemoveMemberCalls).IsEqualTo(1);
    }

    [Test]
    public async Task LeaveOrganizationAsync_EvictsCachedActivationState()
    {
        var inner = new CountingMembershipStore(
            new MembershipActivationState("org_1", "user_1", MembershipStatus.Active, false)
        );
        var cache = new TestHybridCache();
        var store = CreateStore(inner, cache);

        await store.FindMembershipActivationStateAsync("org_1", "user_1", CancellationToken.None);
        inner.State = new MembershipActivationState(
            "org_1",
            "user_1",
            MembershipStatus.Disabled,
            true
        );
        await store.LeaveOrganizationAsync("org_1", "user_1", CancellationToken.None);
        var after = await store.FindMembershipActivationStateAsync(
            "org_1",
            "user_1",
            CancellationToken.None
        );

        await Assert.That(after?.IsActive).IsFalse();
        await Assert.That(inner.ActivationLookupCount).IsEqualTo(2);
    }

    [Test]
    public async Task AcceptInvitationAsync_OnSuccess_EvictsCachedActivationStateByUserTag()
    {
        var inner = new CountingMembershipStore(state: null, acceptResult: true);
        var cache = new TestHybridCache();
        var store = CreateStore(inner, cache);

        // Prime a stale "not active" cache entry, as if a pre-join lookup
        // had happened just before the invitation was accepted. Eviction
        // here does not depend on resolving an organization ID for the
        // membership row — it evicts every cached entry tagged with the
        // user, regardless of organization.
        await store.FindMembershipActivationStateAsync("org_1", "user_1", CancellationToken.None);
        inner.State = new MembershipActivationState(
            "org_1",
            "user_1",
            MembershipStatus.Active,
            false
        );

        var accepted = await store.AcceptInvitationAsync(
            "membership_1",
            "user_1",
            CancellationToken.None
        );
        var after = await store.FindMembershipActivationStateAsync(
            "org_1",
            "user_1",
            CancellationToken.None
        );

        // Guards that a successful accept evicts the stale pre-join cache
        // entry so the member's very first bearer-token request succeeds
        // immediately, instead of a spurious 403 for up to 30 seconds.
        await Assert.That(accepted).IsTrue();
        await Assert.That(after?.IsActive).IsTrue();
        await Assert.That(inner.ActivationLookupCount).IsEqualTo(2);
    }

    [Test]
    public async Task AcceptInvitationAsync_OnSuccess_EvictsCachedStateAcrossAllOrgsForUser()
    {
        var inner = new CountingMembershipStore(
            new MembershipActivationState("org_1", "user_1", MembershipStatus.Active, false),
            acceptResult: true
        );
        var cache = new TestHybridCache();
        var store = CreateStore(inner, cache);

        // Prime cached entries for the same user across two different
        // organizations.
        await store.FindMembershipActivationStateAsync("org_1", "user_1", CancellationToken.None);
        await store.FindMembershipActivationStateAsync("org_2", "user_1", CancellationToken.None);
        await Assert.That(inner.ActivationLookupCount).IsEqualTo(2);

        var accepted = await store.AcceptInvitationAsync(
            "membership_1",
            "user_1",
            CancellationToken.None
        );

        // Guards that accepting an invitation for one organization evicts
        // the user's cached activation state for every organization, not
        // just the one just joined — both cached entries must re-hit the
        // store rather than serve their stale cached values.
        await store.FindMembershipActivationStateAsync("org_1", "user_1", CancellationToken.None);
        await store.FindMembershipActivationStateAsync("org_2", "user_1", CancellationToken.None);

        await Assert.That(accepted).IsTrue();
        await Assert.That(inner.ActivationLookupCount).IsEqualTo(4);
    }

    [Test]
    public async Task AcceptInvitationAsync_OnFailure_DoesNotEvictCache()
    {
        var inner = new CountingMembershipStore(
            new MembershipActivationState("org_1", "user_1", MembershipStatus.Disabled, true),
            acceptResult: false
        );
        var cache = new TestHybridCache();
        var store = CreateStore(inner, cache);

        await store.FindMembershipActivationStateAsync("org_1", "user_1", CancellationToken.None);
        var accepted = await store.AcceptInvitationAsync(
            "membership_1",
            "user_1",
            CancellationToken.None
        );
        await store.FindMembershipActivationStateAsync("org_1", "user_1", CancellationToken.None);

        await Assert.That(accepted).IsFalse();
        // Still only one activation lookup — the second call hit the cache
        // because a failed accept must not evict.
        await Assert.That(inner.ActivationLookupCount).IsEqualTo(1);
    }

    [Test]
    public async Task AcceptInvitationAsync_CacheEvictionThrows_DoesNotFailTheAccept()
    {
        var inner = new CountingMembershipStore(state: null, acceptResult: true);
        var cache = new ThrowingHybridCache();
        var store = CreateStore(inner, cache);

        Func<Task<bool>> act = () =>
            store.AcceptInvitationAsync("membership_1", "user_1", CancellationToken.None);

        await Assert.That(act).ThrowsNothing();
    }

    private static CachedZeeqMembershipStore CreateStore(
        IZeeqMembershipStore inner,
        HybridCache cache
    ) => new(inner, cache, NullLogger<CachedZeeqMembershipStore>.Instance);

    private sealed class CountingMembershipStore(
        MembershipActivationState? state,
        bool acceptResult = false
    ) : IZeeqMembershipStore
    {
        public MembershipActivationState? State { get; set; } = state;
        public int ActivationLookupCount { get; private set; }
        public int RemoveMemberCalls { get; private set; }

        public Task<MembershipActivationState?> FindMembershipActivationStateAsync(
            string orgId,
            string userId,
            CancellationToken ct
        )
        {
            ActivationLookupCount++;
            return Task.FromResult(State);
        }

        public Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct)
        {
            RemoveMemberCalls++;
            return Task.CompletedTask;
        }

        public Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> AcceptInvitationAsync(
            string membershipId,
            string userId,
            CancellationToken ct
        ) => Task.FromResult(acceptResult);

        public Task<bool> AcceptInvitationAsDefaultAsync(
            string membershipId,
            string userId,
            CancellationToken ct
        ) => Task.FromResult(acceptResult);

        public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
            string[] orgIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
            string orgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

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

        public Task<SameDomainInvitationDetails?> FindSameDomainInvitationDetailsAsync(
            string membershipId,
            string email,
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
        private readonly Dictionary<string, HashSet<string>> _tagsByKey = [];

        public HybridCacheEntryOptions? LastOptions { get; private set; }

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        )
        {
            LastOptions = options;
            _tagsByKey[key] = tags?.ToHashSet(StringComparer.Ordinal) ?? [];

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
        )
        {
            foreach (
                var key in _tagsByKey
                    .Where(pair => pair.Value.Contains(tag))
                    .Select(pair => pair.Key)
                    .ToArray()
            )
            {
                _values.Remove(key);
                _tagsByKey.Remove(key);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Simulates an unavailable/broken L2 cache backend so eviction-failure
    /// handling in <see cref="CachedZeeqMembershipStore"/> can be exercised
    /// without a real Postgres-backed <see cref="HybridCache"/>.
    /// </summary>
    private sealed class ThrowingHybridCache : HybridCache
    {
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Simulated cache outage.");

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Simulated cache outage.");

        public override ValueTask RemoveAsync(
            string key,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Simulated cache outage.");

        public override ValueTask RemoveByTagAsync(
            string tag,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Simulated cache outage.");
    }
}
