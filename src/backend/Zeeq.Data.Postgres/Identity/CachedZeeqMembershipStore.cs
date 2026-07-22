using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Zeeq.Data.Postgres.Identity;

/// <summary>
/// Caches the narrow membership-activation lookup used by token validation,
/// while delegating every other membership operation. Mutations that change
/// membership status — removal, self-service leave, and invitation
/// acceptance — evict the cached entry so bearer-token requests reflect the
/// new membership state within one L2 round trip instead of waiting out the
/// TTL in either direction (newly revoked access rejected immediately;
/// newly granted access accepted immediately, so a just-accepted invite
/// doesn't get a confusing 403 from a stale pre-join cache entry).
/// </summary>
/// <remarks>
/// Follows the same decorator shape as
/// <see cref="Zeeq.Data.Postgres.Documents.CachedLibraryDocumentStore"/>: a
/// plain wrapper registered via a graceful-fallback factory, not a Scrutor
/// <c>Decorate&lt;&gt;</c> call. See
/// <c>.agents/plans/2026-07-22-revoke-user-tokens-on-member-removal.spec.md</c>
/// for the full design rationale.
/// <para>
/// Eviction is always attempted after the underlying mutation already
/// succeeded, and is itself best-effort: a cache/eviction failure is logged
/// and swallowed rather than surfaced to the caller, so a transient L2
/// outage cannot turn an already-committed membership change into an
/// apparent handler failure. The 30-second TTL on
/// <see cref="MembershipActivationCacheKeys.CacheOptions"/> is the backstop
/// for a missed eviction either way.
/// </para>
/// </remarks>
internal sealed partial class CachedZeeqMembershipStore(
    IZeeqMembershipStore inner,
    HybridCache cache,
    ILogger<CachedZeeqMembershipStore> logger
) : IZeeqMembershipStore
{
    /// <inheritdoc />
    public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
        inner.FindOrganizationByIdAsync(orgId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
        string[] orgIds,
        CancellationToken ct
    ) => inner.FindOrganizationsByIdsAsync(orgIds, ct);

    /// <inheritdoc />
    public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
        inner.FindOrganizationBySlugAsync(slug, ct);

    /// <inheritdoc />
    public Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
        string orgId,
        CancellationToken ct
    ) => inner.FindOrganizationActivationStateAsync(orgId, ct);

    /// <inheritdoc />
    public Task<bool> IsSlugAvailableAsync(
        string slug,
        string? excludeOrgId,
        CancellationToken ct
    ) => inner.IsSlugAvailableAsync(slug, excludeOrgId, ct);

    /// <inheritdoc />
    public Task UpdateOrganizationAsync(Organization org, CancellationToken ct) =>
        inner.UpdateOrganizationAsync(org, ct);

    /// <inheritdoc />
    public Task<bool> UpdateOrganizationSameDomainOnboardingAsync(
        Organization organization,
        CancellationToken ct
    ) => inner.UpdateOrganizationSameDomainOnboardingAsync(organization, ct);

    /// <inheritdoc />
    public Task<string?> FindUserEmailByIdAsync(string userId, CancellationToken ct) =>
        inner.FindUserEmailByIdAsync(userId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string?>> FindUserEmailsByIdsAsync(
        string[] userIds,
        CancellationToken ct
    ) => inner.FindUserEmailsByIdsAsync(userIds, ct);

    /// <inheritdoc />
    public Task<bool> IsAutoInviteSameDomainAvailableAsync(
        string domain,
        string excludeOrgId,
        CancellationToken ct
    ) => inner.IsAutoInviteSameDomainAvailableAsync(domain, excludeOrgId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> FindAutoInviteSameDomainClaimsAsync(
        string[] domains,
        CancellationToken ct
    ) => inner.FindAutoInviteSameDomainClaimsAsync(domains, ct);

    /// <inheritdoc />
    public Task<int> CountOrganizationsCreatedByUserAsync(string userId, CancellationToken ct) =>
        inner.CountOrganizationsCreatedByUserAsync(userId, ct);

    /// <inheritdoc />
    public Task<Organization?> CreateOrganizationAsync(
        Organization organization,
        Team rootTeam,
        OrganizationMembership ownerMembership,
        TeamMembership rootTeamMembership,
        int maxCreatedOrganizations,
        CancellationToken ct
    ) =>
        inner.CreateOrganizationAsync(
            organization,
            rootTeam,
            ownerMembership,
            rootTeamMembership,
            maxCreatedOrganizations,
            ct
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMembership>> ListActiveMembershipsForUserAsync(
        string userId,
        CancellationToken ct
    ) => inner.ListActiveMembershipsForUserAsync(userId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMember>> ListMembersForOrganizationAsync(
        string orgId,
        CancellationToken ct
    ) => inner.ListMembersForOrganizationAsync(orgId, ct);

    /// <inheritdoc />
    public Task SetDefaultOrganizationAsync(string userId, string orgId, CancellationToken ct) =>
        inner.SetDefaultOrganizationAsync(userId, orgId, ct);

    /// <inheritdoc />
    public Task UpdateMemberRoleAsync(
        string orgId,
        string userId,
        string newRole,
        CancellationToken ct
    ) => inner.UpdateMemberRoleAsync(orgId, userId, newRole, ct);

    /// <inheritdoc />
    public async Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        await inner.RemoveMemberAsync(orgId, userId, ct);
        await TryEvictAsync(orgId, userId);
    }

    /// <inheritdoc />
    public async Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct)
    {
        await inner.LeaveOrganizationAsync(orgId, userId, ct);
        await TryEvictAsync(orgId, userId);
    }

    /// <inheritdoc />
    public Task<string?> FindRootTeamIdForMemberAsync(
        string orgId,
        string userId,
        CancellationToken ct
    ) => inner.FindRootTeamIdForMemberAsync(orgId, userId, ct);

    /// <inheritdoc />
    public Task<OrganizationMembership> CreateInvitationAsync(
        OrganizationMembership invitation,
        CancellationToken ct
    ) => inner.CreateInvitationAsync(invitation, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForEmailAsync(
        string email,
        CancellationToken ct
    ) => inner.ListPendingInvitationsForEmailAsync(email, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForOrganizationAsync(
        string orgId,
        CancellationToken ct
    ) => inner.ListPendingInvitationsForOrganizationAsync(orgId, ct);

    /// <inheritdoc />
    public async Task<bool> AcceptInvitationAsync(
        string membershipId,
        string userId,
        CancellationToken ct
    )
    {
        var accepted = await inner.AcceptInvitationAsync(membershipId, userId, ct);
        if (accepted)
        {
            await TryEvictByUserAsync(userId);
        }

        return accepted;
    }

    /// <inheritdoc />
    public async Task<bool> AcceptInvitationAsDefaultAsync(
        string membershipId,
        string userId,
        CancellationToken ct
    )
    {
        var accepted = await inner.AcceptInvitationAsDefaultAsync(membershipId, userId, ct);
        if (accepted)
        {
            await TryEvictByUserAsync(userId);
        }

        return accepted;
    }

    /// <inheritdoc />
    public Task<SameDomainInvitationDetails?> FindSameDomainInvitationDetailsAsync(
        string membershipId,
        string email,
        CancellationToken ct
    ) => inner.FindSameDomainInvitationDetailsAsync(membershipId, email, ct);

    /// <inheritdoc />
    public Task<bool> DeclineInvitationAsync(string membershipId, CancellationToken ct) =>
        inner.DeclineInvitationAsync(membershipId, ct);

    /// <inheritdoc />
    public Task<bool> CancelInvitationAsync(
        string orgId,
        string membershipId,
        CancellationToken ct
    ) => inner.CancelInvitationAsync(orgId, membershipId, ct);

    /// <inheritdoc />
    public async Task<MembershipActivationState?> FindMembershipActivationStateAsync(
        string orgId,
        string userId,
        CancellationToken ct
    ) =>
        await cache.GetOrCreateAsync(
            MembershipActivationCacheKeys.Build(orgId, userId),
            (inner, orgId, userId),
            static async (state, cancellationToken) =>
                await state.inner.FindMembershipActivationStateAsync(
                    state.orgId,
                    state.userId,
                    cancellationToken
                ),
            MembershipActivationCacheKeys.CacheOptions,
            [MembershipActivationCacheKeys.BuildUserTag(userId)],
            ct
        );

    /// <summary>
    /// Best-effort eviction of a single organization/user activation entry.
    /// A cache failure is logged and swallowed — the mutation this follows
    /// already succeeded, so it must not fail the caller, and the
    /// 30-second TTL bounds the worst case if eviction is lost.
    /// </summary>
    private async Task TryEvictAsync(string orgId, string userId)
    {
        try
        {
            await cache.RemoveAsync(
                MembershipActivationCacheKeys.Build(orgId, userId),
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            LogEvictionFailed(logger, orgId, userId, ex);
        }
    }

    /// <summary>
    /// Best-effort eviction of every cached activation entry for a user,
    /// across all organizations, keyed by the user tag stamped on every
    /// cached entry. Used by invitation acceptance, which has no direct
    /// organization ID to build a single cache key from without an extra
    /// store round trip that could itself race.
    /// </summary>
    private async Task TryEvictByUserAsync(string userId)
    {
        try
        {
            await cache.RemoveByTagAsync(
                MembershipActivationCacheKeys.BuildUserTag(userId),
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            LogEvictionByUserTagFailed(logger, userId, ex);
        }
    }

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Warning,
        Message = "Failed to evict cached membership-activation state. OrganizationId={OrganizationId}, UserId={UserId}"
    )]
    private static partial void LogEvictionFailed(
        ILogger logger,
        string organizationId,
        string userId,
        Exception exception
    );

    [LoggerMessage(
        EventId = 1501,
        Level = LogLevel.Warning,
        Message = "Failed to evict cached membership-activation state by user tag. UserId={UserId}"
    )]
    private static partial void LogEvictionByUserTagFailed(
        ILogger logger,
        string userId,
        Exception exception
    );
}
