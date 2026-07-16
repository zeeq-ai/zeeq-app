using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Runtime.Server.Messaging;

/// <summary>
/// Resolves organization message-routing tiers through HybridCache and Postgres.
/// </summary>
/// <remarks>
/// The messaging platform only owns the <see cref="ITenantTierResolver" />
/// contract. Runtime composition owns this implementation because it depends on
/// the configured cache and concrete Postgres data context.
///
/// Tenant tier controls queue service class. A priority organization routes to
/// priority buckets, a default organization routes to default buckets, and a
/// low-tier organization routes to lower-capacity buckets. That decision happens
/// on the publish path before Brighter writes the message, so a cache miss here
/// would otherwise add a database read to every published tenant message.
///
/// After this resolver returns the tier, the publisher hashes the organization
/// ID into one stable bucket inside that tier. Message priority is a separate
/// axis declared on the message type; it supplies default timeout, buffer,
/// polling, and performer settings. Together, tenant tier decides where the
/// tenant's work is routed, priority decides how aggressively that class of work
/// is processed, and bucket hashing spreads tenants within the selected tier so
/// one organization cannot consume all capacity for that tier.
///
/// Organization tier changes are rare compared with message publication. Caching
/// the <see cref="OrganizationTier"/> by organization ID keeps routing fast and
/// consistent across web and worker modes while still allowing the runtime data
/// layer to remain the source of truth when the cache entry expires or is
/// invalidated.
/// </remarks>
public sealed class HybridCacheTenantTierResolver(HybridCache cache, PostgresDbContext db)
    : ITenantTierResolver
{
    /// <inheritdoc />
    public async ValueTask<OrganizationTier> ResolveTierAsync(
        string organizationId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return OrganizationTier.Default;
        }

        // The cached value is only the routing tier, not the organization entity.
        // That keeps the publish path cheap while avoiding stale copies of
        // unrelated organization data in the messaging layer.
        return await cache.GetOrCreateAsync(
            $"messaging:organization-tier:{organizationId}",
            async ct =>
                await db
                    .Organizations.TagWithOperationCallSite(
                        "messaging.tenant_tier.resolve_organization_tier"
                    )
                    .AsNoTracking()
                    .Where(organization => organization.Id == organizationId)
                    .Select(organization => organization.Tier)
                    .SingleOrDefaultAsync(ct),
            cancellationToken: cancellationToken
        );
    }
}
