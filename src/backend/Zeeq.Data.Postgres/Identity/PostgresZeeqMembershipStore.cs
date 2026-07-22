using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Identity;

/// <inheritdoc cref="IZeeqMembershipStore" />
/// <remarks>
/// Postgres implementation of the membership store.
/// All read methods use <c>AsNoTracking()</c>; write methods use
/// <c>ExecuteUpdateAsync</c> for targeted, non-materializing mutations.
/// </remarks>
internal sealed class PostgresZeeqMembershipStore(PostgresDbContext db) : IZeeqMembershipStore
{
    private const string AutoInviteSameDomainIndexName =
        "ix_core_organizations_auto_invite_same_domain";

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
            .Organizations.TagWithOperationCallSite("membership.organization.find_activation_state")
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
    public async Task<bool> UpdateOrganizationSameDomainOnboardingAsync(
        Organization organization,
        CancellationToken ct
    )
    {
        organization.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var entry = db.Entry(organization);
        if (entry.State == EntityState.Detached)
        {
            db.Organizations.Attach(organization);
            entry = db.Entry(organization);
        }

        entry.State = EntityState.Unchanged;
        entry.Property(org => org.AutoInviteSameDomainEnabled).IsModified = true;
        entry.Property(org => org.AutoInviteSameDomain).IsModified = true;
        entry.Property(org => org.AutoInviteDefaultRole).IsModified = true;
        entry.Property(org => org.UpdatedAtUtc).IsModified = true;

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsAutoInviteSameDomainUniqueViolation(ex))
        {
            entry.State = EntityState.Detached;
            return false;
        }
    }

    /// <inheritdoc />
    public Task<string?> FindUserEmailByIdAsync(string userId, CancellationToken ct) =>
        db
            .Users.TagWithOperationCallSite("membership.user.find_email_by_id")
            .AsNoTracking()
            .Where(user => user.Id == userId && user.DisabledAtUtc == null)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> FindUserEmailsByIdsAsync(
        string[] userIds,
        CancellationToken ct
    )
    {
        var distinctUserIds = userIds.Distinct().ToArray();
        if (distinctUserIds.Length == 0)
        {
            return new Dictionary<string, string?>();
        }

        return await db
            .Users.TagWithOperationCallSite("membership.user.find_emails_by_ids")
            .AsNoTracking()
            .Where(user => distinctUserIds.Contains(user.Id) && user.DisabledAtUtc == null)
            .Select(user => new { user.Id, user.Email })
            .ToDictionaryAsync(user => user.Id, user => user.Email, ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsAutoInviteSameDomainAvailableAsync(
        string domain,
        string excludeOrgId,
        CancellationToken ct
    ) =>
        !await db
            .Organizations.TagWithOperationCallSite(
                "membership.organization.is_auto_invite_same_domain_available"
            )
            .AsNoTracking()
            .AnyAsync(
                organization =>
                    organization.Id != excludeOrgId
                    && organization.AutoInviteSameDomainEnabled
                    && organization.AutoInviteSameDomain == domain
                    && organization.DisabledAtUtc == null,
                ct
            );

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> FindAutoInviteSameDomainClaimsAsync(
        string[] domains,
        CancellationToken ct
    )
    {
        if (domains.Length == 0)
        {
            return new Dictionary<string, string>();
        }

        return await db
            .Organizations.TagWithOperationCallSite(
                "membership.organization.find_auto_invite_same_domain_claims"
            )
            .AsNoTracking()
            .Where(organization =>
                organization.AutoInviteSameDomainEnabled
                && organization.AutoInviteSameDomain != null
                && domains.Contains(organization.AutoInviteSameDomain)
                && organization.DisabledAtUtc == null
            )
            .Select(organization => new
            {
                Domain = organization.AutoInviteSameDomain!,
                OrganizationId = organization.Id,
            })
            .ToDictionaryAsync(row => row.Domain, row => row.OrganizationId, ct);
    }

    /// <inheritdoc />
    public Task<int> CountOrganizationsCreatedByUserAsync(string userId, CancellationToken ct) =>
        db
            .Organizations.TagWithOperationCallSite("membership.organization.count_created_by_user")
            .AsNoTracking()
            .CountAsync(o => o.CreatedByUserId == userId, ct);

    private static bool IsAutoInviteSameDomainUniqueViolation(DbUpdateException ex) =>
        ex.InnerException
            is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: AutoInviteSameDomainIndexName,
            };

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

        return
        [
            .. rows.Select(x => new OrganizationMember(
                x.Id,
                x.DisplayName,
                x.Email,
                x.PictureUrl,
                x.Role,
                x.CreatedAtUtc
            )),
        ];
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
    )
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();

        return await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.invitation.list_pending_for_email"
            )
            .AsNoTracking()
            .Where(m =>
                m.InvitedEmail != null
                && m.InvitedEmail.ToLower() == normalizedEmail
                && m.Status == MembershipStatus.Pending
                && m.DisabledAtUtc == null
                && m.ExpiresAtUtc > DateTimeOffset.UtcNow
            )
            .OrderBy(m => m.CreatedAtUtc)
            .ToArrayAsync(ct);
    }

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
    public async Task<SameDomainInvitationDetails?> FindSameDomainInvitationDetailsAsync(
        string membershipId,
        string email,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var invitedDomain = EmailDomainNormalizer.FromEmail(normalizedEmail);
        if (invitedDomain is null || PublicEmailDomainCatalog.IsPublicEmailDomain(invitedDomain))
        {
            return null;
        }

        return await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.invitation.find_same_domain_details"
            )
            .AsNoTracking()
            .Where(m =>
                m.Id == membershipId
                && m.InvitedEmail != null
                && m.InvitedEmail.ToLower() == normalizedEmail
                // NOTE: This lookup only powers the same-domain onboarding details screen.
                // General invitation acceptance validates manual invitations through
                // ListPendingInvitationsForEmailAsync before accepting the membership.
                && m.IsSameDomainAutoInvite
                && m.Status == MembershipStatus.Pending
                && m.DisabledAtUtc == null
                && m.ExpiresAtUtc > now
            )
            .Join(
                db.Organizations.AsNoTracking(),
                membership => membership.OrganizationId,
                organization => organization.Id,
                (membership, organization) =>
                    new { Membership = membership, Organization = organization }
            )
            .Where(row =>
                row.Organization.DisabledAtUtc == null
                && row.Organization.AutoInviteSameDomainEnabled
                && row.Organization.AutoInviteSameDomain == invitedDomain
            )
            .Join(
                db.Users.AsNoTracking(),
                row => row.Organization.CreatedByUserId,
                owner => owner.Id,
                (row, owner) =>
                    new
                    {
                        row.Membership,
                        row.Organization,
                        Owner = owner,
                    }
            )
            .Where(row => row.Owner.DisabledAtUtc == null)
            .Select(row => new SameDomainInvitationDetails(
                row.Membership.Id,
                row.Organization.Id,
                row.Organization.DisplayName,
                row.Organization.IconUrl,
                row.Owner.Id,
                row.Owner.DisplayName,
                row.Owner.Email,
                row.Owner.PictureUrl,
                row.Membership.Role
            ))
            .SingleOrDefaultAsync(ct);
    }

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
    ) => await AcceptInvitationCoreAsync(membershipId, userId, setAsDefault: false, ct);

    /// <inheritdoc />
    public async Task<bool> AcceptInvitationAsDefaultAsync(
        string membershipId,
        string userId,
        CancellationToken ct
    ) => await AcceptInvitationCoreAsync(membershipId, userId, setAsDefault: true, ct);

    private async Task<bool> AcceptInvitationCoreAsync(
        string membershipId,
        string userId,
        bool setAsDefault,
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

        if (setAsDefault)
        {
            await db
                .OrganizationMemberships.TagWithOperationCallSite(
                    "membership.invitation.accept_as_default_clear_existing"
                )
                .Where(m =>
                    m.UserId == userId && m.IsDefault && m.Status == MembershipStatus.Active
                )
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsDefault, false), ct);

            await db
                .OrganizationMemberships.TagWithOperationCallSite(
                    "membership.invitation.accept_as_default_apply"
                )
                .Where(m =>
                    m.Id == membershipId
                    && m.UserId == userId
                    && m.Status == MembershipStatus.Active
                )
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsDefault, true), ct);
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

    // ── Token-Validation Membership Check ───────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Removed memberships are retained (soft-deleted) for audit history, so
    /// a user who was removed and later re-invited to the same organization
    /// can legally have more than one row for the same
    /// <c>(OrganizationId, UserId)</c> pair — the unique index only
    /// constrains active rows. Ordering active-first, then most recent, and
    /// taking the first row keeps this a single query while still
    /// preferring the live membership over historical ones.
    /// </remarks>
    public Task<MembershipActivationState?> FindMembershipActivationStateAsync(
        string orgId,
        string userId,
        CancellationToken ct
    ) =>
        db
            .OrganizationMemberships.TagWithOperationCallSite(
                "membership.organization_membership.find_activation_state"
            )
            .AsNoTracking()
            .Where(m => m.OrganizationId == orgId && m.UserId == userId)
            .OrderByDescending(m => m.Status == MembershipStatus.Active && m.DisabledAtUtc == null)
            .ThenByDescending(m => m.CreatedAtUtc)
            .Select(m => new MembershipActivationState(
                m.OrganizationId,
                userId,
                m.Status,
                m.DisabledAtUtc != null
            ))
            .FirstOrDefaultAsync(ct);
}
