using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Identity;

/// <inheritdoc cref="IZeeqMembershipStore" />
/// <remarks>
/// Postgres implementation of the membership store.
/// All read methods use <c>AsNoTracking()</c>; write methods use
/// <c>ExecuteUpdateAsync</c> for targeted, non-materializing mutations.
/// </remarks>
internal sealed class PostgresZeeqMembershipStore(PostgresDbContext db) : IZeeqMembershipStore
{
    // ── Organizations ──────────────────────────────────────────

    /// <inheritdoc />
    public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
        db
            .Organizations.TagWithOperationCallSite("membership.organization.find_by_id")
            .AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == orgId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
        string[] orgIds,
        CancellationToken ct
    )
    {
        if (orgIds.Length == 0)
            return [];

        return await db
            .Organizations.TagWithOperationCallSite("membership.organization.find_by_ids")
            .AsNoTracking()
            .Where(o => orgIds.Contains(o.Id))
            .ToArrayAsync(ct);
    }

    /// <inheritdoc />
    public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
        db
            .Organizations.TagWithOperationCallSite("membership.organization.find_by_slug")
            .AsNoTracking()
            .SingleOrDefaultAsync(o => o.Slug == slug, ct);

    /// <inheritdoc />
    public Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
        string orgId,
        CancellationToken ct
    ) =>
        db
            .Organizations.TagWithOperationCallSite(
                "membership.organization.find_activation_state"
            )
            .AsNoTracking()
            .Where(organization => organization.Id == orgId)
            .Select(organization => new OrganizationActivationState(
                organization.Id,
                organization.ActivatedAtUtc,
                organization.DisabledAtUtc
            ))
            .SingleOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task<bool> IsSlugAvailableAsync(
        string slug,
        string? excludeOrgId,
        CancellationToken ct
    )
    {
        var query = db
            .Organizations.TagWithOperationCallSite("membership.organization.is_slug_available")
            .Where(o => o.Slug == slug);
        if (excludeOrgId is not null)
            query = query.Where(o => o.Id != excludeOrgId);

        return !await query.AnyAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdateOrganizationAsync(Organization org, CancellationToken ct)
    {
        org.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.Organizations.Update(org);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public Task<int> CountOrganizationsCreatedByUserAsync(string userId, CancellationToken ct) =>
        db
            .Organizations.TagWithOperationCallSite("membership.organization.count_created_by_user")
            .AsNoTracking()
            .CountAsync(o => o.CreatedByUserId == userId, ct);

    /// <inheritdoc />
    public async Task<Organization?> CreateOrganizationAsync(
        Organization organization,
        Team rootTeam,
        OrganizationMembership ownerMembership,
        TeamMembership rootTeamMembership,
        int maxCreatedOrganizations,
        CancellationToken ct
    )
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(722451, hashtext({organization.CreatedByUserId}))",
            ct
        );

        var createdCount = await db
            .Organizations.TagWithOperationCallSite("membership.organization.create_count_existing")
            .CountAsync(o => o.CreatedByUserId == organization.CreatedByUserId, ct);

        if (createdCount >= maxCreatedOrganizations)
        {
            return null;
        }

        db.Organizations.Add(organization);
        db.Teams.Add(rootTeam);
        db.OrganizationMemberships.Add(ownerMembership);
        db.TeamMemberships.Add(rootTeamMembership);

        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        return organization;
    }

    // ── Memberships ────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Filters to the current user, active status, non-disabled rows,
    /// and excludes organizations where <c>DisabledAtUtc</c> is set.
    /// Ordered by creation time so the first org is the oldest
    /// (consistent default when no explicit default is set).
    /// </remarks>
    public async Task<IReadOnlyList<OrganizationMembership>> ListActiveMembershipsForUserAsync(
        string userId,
        CancellationToken ct
    ) =>
        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.list_active_for_user"
            )
            .AsNoTracking()
            .Where(m =>
                m.UserId == userId && m.Status == MembershipStatus.Active && m.DisabledAtUtc == null
            )
            .Join(
                db.Organizations.TagWithOperationCallSite(
                        "membership.organization_membership.list_active_for_user_join_organization"
                    )
                    .AsNoTracking(),
                m => m.OrganizationId,
                o => o.Id,
                (m, o) => new { Membership = m, Organization = o }
            )
            .Where(x => x.Organization.DisabledAtUtc == null)
            .OrderBy(x => x.Membership.CreatedAtUtc)
            .Select(x => x.Membership)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganizationMember>> ListMembersForOrganizationAsync(
        string orgId,
        CancellationToken ct
    )
    {
        var rows = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.list_members_for_organization"
            )
            .AsNoTracking()
            .Where(m =>
                m.OrganizationId == orgId
                && m.Status == MembershipStatus.Active
                && m.DisabledAtUtc == null
                && m.UserId != null
            )
            .Join(
                db.Users,
                m => m.UserId,
                u => u.Id,
                (m, u) =>
                    new
                    {
                        u.Id,
                        u.DisplayName,
                        u.Email,
                        u.PictureUrl,
                        m.Role,
                        m.CreatedAtUtc,
                    }
            )
            .OrderBy(x => x.DisplayName)
            .ToArrayAsync(ct);

        return rows.Select(x => new OrganizationMember(
                x.Id,
                x.DisplayName,
                x.Email,
                x.PictureUrl,
                x.Role,
                x.CreatedAtUtc
            ))
            .ToArray();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Executes two atomic updates in a transaction: unsets all existing
    /// defaults for the user, then sets the target org as default.
    /// Rolls back if the target membership no longer exists at write time
    /// (prevents silently losing the user's default org).
    /// </remarks>
    public async Task SetDefaultOrganizationAsync(string userId, string orgId, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        // Unset any existing default
        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.set_default_clear_existing"
            )
            .Where(m => m.UserId == userId && m.IsDefault && m.Status == MembershipStatus.Active)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsDefault, false), ct);

        // Set the new default — must affect exactly one row
        var affected = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.set_default_apply"
            )
            .Where(m =>
                m.OrganizationId == orgId
                && m.UserId == userId
                && m.Status == MembershipStatus.Active
            )
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsDefault, true), ct);

        if (affected != 1)
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(ct);
            }

            throw new InvalidOperationException(
                "Target membership was not found or is no longer active."
            );
        }

        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Updates the role via ExecuteUpdateAsync — no materialization needed.
    /// Caller must validate the role value before invoking.
    /// </remarks>
    public async Task UpdateMemberRoleAsync(
        string orgId,
        string userId,
        string newRole,
        CancellationToken ct
    )
    {
        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.update_role"
            )
            .Where(m =>
                m.OrganizationId == orgId
                && m.UserId == userId
                && m.Status == MembershipStatus.Active
            )
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, newRole), ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Soft-deletes a membership: Status → Disabled, records timestamp,
    /// clears the default flag. Does not physically delete the row.
    /// </remarks>
    public async Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.remove_member"
            )
            .Where(m =>
                m.OrganizationId == orgId
                && m.UserId == userId
                && m.Status == MembershipStatus.Active
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(m => m.Status, MembershipStatus.Disabled)
                        .SetProperty(m => m.DisabledAtUtc, now)
                        .SetProperty(m => m.IsDefault, false),
                ct
            );
    }

    /// <inheritdoc />
    /// <remarks>
    /// Self-service leave — same soft-delete semantics as
    /// <see cref="RemoveMemberAsync"/> but called by the member themselves.
    /// Last-owner enforcement is the caller's responsibility.
    /// </remarks>
    public async Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.leave_organization"
            )
            .Where(m =>
                m.OrganizationId == orgId
                && m.UserId == userId
                && m.Status == MembershipStatus.Active
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(m => m.Status, MembershipStatus.Disabled)
                        .SetProperty(m => m.DisabledAtUtc, now)
                        .SetProperty(m => m.IsDefault, false),
                ct
            );
    }

    /// <inheritdoc />
    public Task<string?> FindRootTeamIdForMemberAsync(
        string orgId,
        string userId,
        CancellationToken ct
    ) =>
        db
            .TeamMemberships.TagWithOperationCallSite(
                "membership.team_membership.find_root_team_id"
            )
            .AsNoTracking()
            .Where(m => m.OrganizationId == orgId && m.UserId == userId && m.DisabledAtUtc == null)
            .Join(
                db.Teams.TagWithOperationCallSite(
                        "membership.team_membership.find_root_team_id_join_team"
                    )
                    .AsNoTracking(),
                m => new { m.OrganizationId, Id = m.TeamId },
                t => new { t.OrganizationId, t.Id },
                (m, t) => new { Membership = m, Team = t }
            )
            .Where(x => x.Team.IsRootTeam && x.Team.DisabledAtUtc == null)
            .Select(x => x.Membership.TeamId)
            .SingleOrDefaultAsync(ct);

    // ── Invitations ────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Inserts a pending membership row with UserId = null. The caller
    /// sets Status = MembershipStatus.Pending, InvitedEmail, Role, and ExpiresAtUtc.
    /// </remarks>
    public async Task<OrganizationMembership> CreateInvitationAsync(
        OrganizationMembership invitation,
        CancellationToken ct
    )
    {
        db.OrganizationMemberships.Add(invitation);
        await db.SaveChangesAsync(ct);

        return invitation;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lists unexpired pending invitations for an email address.
    /// Used by the "My Invitations" list in the UI.
    /// </remarks>
    public async Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForEmailAsync(
        string email,
        CancellationToken ct
    ) =>
        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.invitation.list_pending_for_email"
            )
            .AsNoTracking()
            .Where(m =>
                m.InvitedEmail == email
                && m.Status == MembershipStatus.Pending
                && m.DisabledAtUtc == null
                && m.ExpiresAtUtc > DateTimeOffset.UtcNow
            )
            .OrderBy(m => m.CreatedAtUtc)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    /// Lists outgoing pending invitations for the organization member-management UI.
    /// </remarks>
    public async Task<
        IReadOnlyList<OrganizationMembership>
    > ListPendingInvitationsForOrganizationAsync(string orgId, CancellationToken ct) =>
        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.invitation.list_pending_for_organization"
            )
            .AsNoTracking()
            .Where(m =>
                m.OrganizationId == orgId
                && m.Status == MembershipStatus.Pending
                && m.DisabledAtUtc == null
                && m.ExpiresAtUtc > DateTimeOffset.UtcNow
            )
            .OrderBy(m => m.InvitedEmail)
            .ThenBy(m => m.CreatedAtUtc)
            .ToArrayAsync(ct);

    /// <inheritdoc />
    /// <remarks>
    /// Atomically transitions a pending row to active and adds the user to the
    /// organization's root team, after verifying the user does not already have
    /// an active membership in the same organization (which would violate the
    /// filtered unique index).
    /// Returns <c>true</c> if the row was updated.
    /// </remarks>
    public async Task<bool> AcceptInvitationAsync(
        string membershipId,
        string userId,
        CancellationToken ct
    )
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        // Load the invitation to discover the target org
        var invitation = await db
            .OrganizationMemberships.TagWithOperationCallSite("membership.invitation.accept_find")
            .AsNoTracking()
            .SingleOrDefaultAsync(
                m => m.Id == membershipId && m.Status == MembershipStatus.Pending,
                ct
            );

        if (invitation is null)
            return false;

        // Guard: if the user is already an active member of this org,
        // decline the invitation instead of hitting the unique constraint
        var alreadyActive = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.invitation.accept_check_existing_membership"
            )
            .AnyAsync(
                m =>
                    m.OrganizationId == invitation.OrganizationId
                    && m.UserId == userId
                    && m.Status == MembershipStatus.Active
                    && m.DisabledAtUtc == null,
                ct
            );

        if (alreadyActive)
        {
            await db
                .OrganizationMemberships.TagWithOperationCallSite(
                    "membership.invitation.accept_decline_duplicate"
                )
                .Where(m => m.Id == membershipId)
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(m => m.Status, MembershipStatus.Declined)
                            .SetProperty(m => m.DisabledAtUtc, DateTimeOffset.UtcNow),
                    ct
                );

            if (tx is not null)
            {
                await tx.CommitAsync(ct);
            }

            return false;
        }

        var rootTeamId = await db
            .Teams.TagWithOperationCallSite("membership.invitation.accept_find_root_team")
            .AsNoTracking()
            .Where(t =>
                t.OrganizationId == invitation.OrganizationId
                && t.IsRootTeam
                && t.DisabledAtUtc == null
            )
            .Select(t => t.Id)
            .SingleOrDefaultAsync(ct);

        if (rootTeamId is null)
        {
            return false;
        }

        var affected = await db
            .OrganizationMemberships.TagWithOperationCallSite("membership.invitation.accept_apply")
            .Where(m => m.Id == membershipId && m.Status == MembershipStatus.Pending)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(m => m.UserId, userId)
                        .SetProperty(m => m.Status, MembershipStatus.Active)
                        .SetProperty(m => m.ExpiresAtUtc, (DateTimeOffset?)null)
                        .SetProperty(m => m.DisabledAtUtc, (DateTimeOffset?)null),
                ct
            );

        if (affected != 1)
        {
            return false;
        }

        var hasRootTeamMembership = await db
            .TeamMemberships.TagWithOperationCallSite(
                "membership.invitation.accept_check_root_team_membership"
            )
            .AnyAsync(
                m =>
                    m.OrganizationId == invitation.OrganizationId
                    && m.TeamId == rootTeamId
                    && m.UserId == userId,
                ct
            );

        if (!hasRootTeamMembership)
        {
            db.TeamMemberships.Add(
                new TeamMembership
                {
                    OrganizationId = invitation.OrganizationId,
                    TeamId = rootTeamId,
                    UserId = userId,
                    Role = invitation.Role,
                    CreatedByUserId = invitation.CreatedByUserId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                }
            );
        }

        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Atomically transitions a pending row to declined.
    /// Returns <c>true</c> if a row was updated.
    /// </remarks>
    public async Task<bool> DeclineInvitationAsync(string membershipId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await db
            .OrganizationMemberships.TagWithOperationCallSite("membership.invitation.decline")
            .Where(m => m.Id == membershipId && m.Status == MembershipStatus.Pending)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(m => m.Status, MembershipStatus.Declined)
                        .SetProperty(m => m.DisabledAtUtc, now),
                ct
            );

        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Soft-cancels a sent invitation while preserving the row for audit history.
    /// </remarks>
    public async Task<bool> CancelInvitationAsync(
        string orgId,
        string membershipId,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await db
            .OrganizationMemberships.TagWithOperationCallSite("membership.invitation.cancel")
            .Where(m =>
                m.Id == membershipId
                && m.OrganizationId == orgId
                && m.Status == MembershipStatus.Pending
                && m.DisabledAtUtc == null
                && m.ExpiresAtUtc > now
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(m => m.Status, MembershipStatus.Declined)
                        .SetProperty(m => m.DisabledAtUtc, now),
                ct
            );

        return affected > 0;
    }
}
