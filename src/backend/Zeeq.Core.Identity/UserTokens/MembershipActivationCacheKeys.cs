using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Core.Identity;

/// <summary>
/// Shared cache-key construction and entry options for the token-validation
/// membership-activation check, so the read side (token validation
/// middleware, in this assembly) and the write side (membership store
/// eviction, in <c>Zeeq.Data.Postgres</c>) agree on the exact key without a
/// cross-assembly constant duplication.
/// </summary>
/// <remarks>
/// L2-only (<see cref="HybridCacheEntryFlags.DisableLocalCache"/>) because
/// bearer tokens can be long-lived (up to 730 days) and are used from
/// multiple <c>zeeq-server</c> replicas; an in-process L1 entry could let one
/// replica keep honoring a revoked membership past its own local TTL while
/// another replica has already evicted the key. The 30-second L2 TTL bounds
/// the worst-case staleness window when eviction itself is skipped (e.g. a
/// non-fatal token-revoke failure during member removal).
/// <para>
/// Public rather than <see langword="internal"/>: <c>Zeeq.Core.Identity</c>
/// has no <c>InternalsVisibleTo</c> entry for <c>Zeeq.Data.Postgres</c>, and
/// adding one solely for this constant is more invasive than making the
/// helper itself additive-only public API.
/// </para>
/// </remarks>
public static class MembershipActivationCacheKeys
{
    private const string KeyPrefix = "identity:membership-activation-state:";

    /// <summary>
    /// Cache entry options shared by every read/write of the
    /// membership-activation cache entry.
    /// </summary>
    public static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        Flags = HybridCacheEntryFlags.DisableLocalCache,
    };

    private const string UserTagPrefix = "identity:membership-activation-state:user:";

    /// <summary>
    /// Builds the cache key for a given organization/user membership pair.
    /// </summary>
    /// <param name="organizationId">Organization the membership belongs to.</param>
    /// <param name="userId">Local user ID whose membership state is cached.</param>
    public static string Build(string organizationId, string userId) =>
        $"{KeyPrefix}{organizationId}:{userId}";

    /// <summary>
    /// Builds the per-user cache tag every membership-activation entry for
    /// that user is stamped with, so a user's entries across every
    /// organization can be evicted without knowing which organization ID a
    /// given mutation applies to.
    /// </summary>
    /// <param name="userId">Local user ID the cached entries belong to.</param>
    /// <remarks>
    /// Used by invitation acceptance: accepting is keyed by membership row
    /// ID, not <c>(organizationId, userId)</c>, so evicting by key would
    /// require an extra store round trip to resolve the organization ID —
    /// one that could itself miss a genuine race (row already gone) and
    /// leave a stale entry for the rest of the TTL. Tag-based eviction
    /// removes that dependency entirely.
    /// </remarks>
    public static string BuildUserTag(string userId) => $"{UserTagPrefix}{userId}";
}
