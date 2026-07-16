using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <inheritdoc cref="IZeeqMembershipStore" />
/// <remarks>
/// Minimal in-memory store for handler branch tests.
/// <para>
/// The production store invariants and database behavior are covered by
/// <see cref="MembershipIntegrationTestBase"/> descendants. This fake only
/// models the small persistence surface needed to prove handler validation,
/// gating, and response selection.
/// </para>
/// </remarks>
internal sealed class TestMembershipStore : IZeeqMembershipStore
{
    public List<Organization> Organizations { get; } = [];
    public List<OrganizationMembership> Memberships { get; } = [];
    public List<Team> Teams { get; } = [];
    public List<TeamMembership> TeamMemberships { get; } = [];
    public List<OrganizationMembership> Invitations { get; } = [];
    public List<OrganizationMember> Members { get; } = [];
    public int LeaveOrganizationCalls { get; private set; }

    /// <summary>
    /// Adds a generated seed graph to the in-memory store.
    /// </summary>
    /// <param name="seed">Seed graph to add.</param>
    /// <returns>This store for fluent test setup.</returns>
    public TestMembershipStore AddSeed(SeedContext seed)
    {
        Organizations.Add(seed.Organization);
        Teams.Add(seed.RootTeam);
        Memberships.AddRange(seed.OrganizationMemberships);
        TeamMemberships.AddRange(seed.TeamMemberships);

        return this;
    }

    /// <summary>
    /// Adds generated organization graphs to the in-memory store.
    /// </summary>
    /// <param name="organizationGraphs">Organization graphs to add.</param>
    /// <returns>This store for fluent test setup.</returns>
    public TestMembershipStore AddOrganizationGraphs(params OrganizationGraph[] organizationGraphs)
    {
        foreach (var graph in organizationGraphs)
        {
            Organizations.Add(graph.Organization);
            Teams.Add(graph.RootTeam);
            Memberships.Add(graph.OrganizationMembership);
            TeamMemberships.Add(graph.RootTeamMembership);
        }

        return this;
    }

    /// <summary>
    /// Adds pending invitation rows to the in-memory store.
    /// </summary>
    /// <param name="invitations">Invitation rows to add.</param>
    /// <returns>This store for fluent test setup.</returns>
    public TestMembershipStore AddInvitations(params OrganizationMembership[] invitations)
    {
        Invitations.AddRange(invitations);

        return this;
    }

    /// <inheritdoc />
    public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
        Task.FromResult(Organizations.SingleOrDefault(o => o.Id == orgId));

    /// <inheritdoc />
    public Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
        string[] orgIds,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<Organization>>(
            Organizations.Where(o => orgIds.Contains(o.Id)).ToArray()
        );

    /// <inheritdoc />
    public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Organizations.SingleOrDefault(o => o.Slug == slug));

    /// <inheritdoc />
    public Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
        string orgId,
        CancellationToken ct
    )
    {
        var organization = Organizations.SingleOrDefault(o => o.Id == orgId);

        return Task.FromResult(
            organization is null
                ? null
                : new OrganizationActivationState(
                    organization.Id,
                    organization.ActivatedAtUtc,
                    organization.DisabledAtUtc
                )
        );
    }

    /// <inheritdoc />
    public Task<bool> IsSlugAvailableAsync(
        string slug,
        string? excludeOrgId,
        CancellationToken ct
    ) => Task.FromResult(!Organizations.Any(o => o.Slug == slug && o.Id != excludeOrgId));

    /// <inheritdoc />
    public Task UpdateOrganizationAsync(Organization org, CancellationToken ct) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<int> CountOrganizationsCreatedByUserAsync(string userId, CancellationToken ct) =>
        Task.FromResult(Organizations.Count(org => org.CreatedByUserId == userId));

    /// <inheritdoc />
    public Task<Organization?> CreateOrganizationAsync(
        Organization organization,
        Team rootTeam,
        OrganizationMembership ownerMembership,
        TeamMembership rootTeamMembership,
        int maxCreatedOrganizations,
        CancellationToken ct
    )
    {
        if (
            Organizations.Count(org => org.CreatedByUserId == organization.CreatedByUserId)
            >= maxCreatedOrganizations
        )
        {
            return Task.FromResult<Organization?>(null);
        }

        Organizations.Add(organization);
        Teams.Add(rootTeam);
        Memberships.Add(ownerMembership);
        TeamMemberships.Add(rootTeamMembership);

        return Task.FromResult<Organization?>(organization);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMembership>> ListActiveMembershipsForUserAsync(
        string userId,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<OrganizationMembership>>([
            .. Memberships.Where(m =>
                m.UserId == userId && m.Status == MembershipStatus.Active && m.DisabledAtUtc is null
            ),
        ]);

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMember>> ListMembersForOrganizationAsync(
        string orgId,
        CancellationToken ct
    ) => Task.FromResult<IReadOnlyList<OrganizationMember>>(Members.ToArray());

    /// <inheritdoc />
    public Task SetDefaultOrganizationAsync(string userId, string orgId, CancellationToken ct)
    {
        var memberships = Memberships
            .Where(m =>
                m.UserId == userId && m.Status == MembershipStatus.Active && m.DisabledAtUtc is null
            )
            .ToArray();

        if (!memberships.Any(m => m.OrganizationId == orgId))
        {
            throw new InvalidOperationException(
                "Target membership was not found or is no longer active."
            );
        }

        foreach (var membership in memberships)
        {
            membership.IsDefault = membership.OrganizationId == orgId;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateMemberRoleAsync(
        string orgId,
        string userId,
        string newRole,
        CancellationToken ct
    )
    {
        var membership = Memberships.SingleOrDefault(m =>
            m.OrganizationId == orgId && m.UserId == userId
        );

        membership?.Role = newRole;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        var membership = Memberships.SingleOrDefault(m =>
            m.OrganizationId == orgId && m.UserId == userId
        );

        if (membership is not null)
        {
            membership.Status = MembershipStatus.Disabled;
            membership.DisabledAtUtc = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct)
    {
        LeaveOrganizationCalls++;
        return RemoveMemberAsync(orgId, userId, ct);
    }

    /// <inheritdoc />
    public Task<string?> FindRootTeamIdForMemberAsync(
        string orgId,
        string userId,
        CancellationToken ct
    ) =>
        Task.FromResult(
            TeamMemberships
                .Join(
                    Teams,
                    membership => new { membership.OrganizationId, Id = membership.TeamId },
                    team => new { team.OrganizationId, team.Id },
                    (membership, team) => new { membership, team }
                )
                .Where(x =>
                    x.membership.OrganizationId == orgId
                    && x.membership.UserId == userId
                    && x.membership.DisabledAtUtc is null
                    && x.team.IsRootTeam
                    && x.team.DisabledAtUtc is null
                )
                .Select(x => x.membership.TeamId)
                .SingleOrDefault()
        );

    /// <inheritdoc />
    public Task<OrganizationMembership> CreateInvitationAsync(
        OrganizationMembership invitation,
        CancellationToken ct
    )
    {
        Invitations.Add(invitation);
        return Task.FromResult(invitation);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForEmailAsync(
        string email,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<OrganizationMembership>>(
            Invitations
                .Where(i =>
                    i.InvitedEmail == email
                    && i.Status == MembershipStatus.Pending
                    && i.DisabledAtUtc is null
                    && i.ExpiresAtUtc > DateTimeOffset.UtcNow
                )
                .ToArray()
        );

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForOrganizationAsync(
        string orgId,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<OrganizationMembership>>(
            Invitations
                .Where(i =>
                    i.OrganizationId == orgId
                    && i.Status == MembershipStatus.Pending
                    && i.DisabledAtUtc is null
                    && i.ExpiresAtUtc > DateTimeOffset.UtcNow
                )
                .ToArray()
        );

    /// <inheritdoc />
    public Task<bool> AcceptInvitationAsync(
        string membershipId,
        string userId,
        CancellationToken ct
    )
    {
        var invitation = Invitations.SingleOrDefault(i =>
            i.Id == membershipId && i.Status == MembershipStatus.Pending
        );
        if (invitation is null)
        {
            return Task.FromResult(false);
        }

        var rootTeam = Teams.SingleOrDefault(t =>
            t.OrganizationId == invitation.OrganizationId && t.IsRootTeam && t.DisabledAtUtc is null
        );

        if (rootTeam is null)
        {
            return Task.FromResult(false);
        }

        if (
            Memberships.Any(i =>
                i.OrganizationId == invitation.OrganizationId
                && i.UserId == userId
                && i.Status == MembershipStatus.Active
                && i.DisabledAtUtc is null
            )
        )
        {
            invitation.Status = MembershipStatus.Declined;
            invitation.DisabledAtUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(false);
        }

        var acceptedMembership = new OrganizationMembership
        {
            Id = invitation.Id,
            OrganizationId = invitation.OrganizationId,
            UserId = userId,
            Role = invitation.Role,
            Status = MembershipStatus.Active,
            InvitedEmail = invitation.InvitedEmail,
            CreatedByUserId = invitation.CreatedByUserId,
            CreatedAtUtc = invitation.CreatedAtUtc,
            ExpiresAtUtc = null,
            DisabledAtUtc = null,
        };

        Invitations.Remove(invitation);
        Invitations.Add(acceptedMembership);
        Memberships.Add(acceptedMembership);

        if (
            !TeamMemberships.Any(m =>
                m.OrganizationId == invitation.OrganizationId
                && m.TeamId == rootTeam.Id
                && m.UserId == userId
            )
        )
        {
            TeamMemberships.Add(
                new TeamMembership
                {
                    OrganizationId = invitation.OrganizationId,
                    TeamId = rootTeam.Id,
                    UserId = userId,
                    Role = invitation.Role,
                    CreatedByUserId = invitation.CreatedByUserId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                }
            );
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> DeclineInvitationAsync(string membershipId, CancellationToken ct)
    {
        var invitation = Invitations.SingleOrDefault(i =>
            i.Id == membershipId && i.Status == MembershipStatus.Pending
        );

        if (invitation is null)
        {
            return Task.FromResult(false);
        }

        invitation.Status = MembershipStatus.Declined;
        invitation.DisabledAtUtc = DateTimeOffset.UtcNow;

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> CancelInvitationAsync(string orgId, string membershipId, CancellationToken ct)
    {
        var invitation = Invitations.SingleOrDefault(i =>
            i.Id == membershipId
            && i.OrganizationId == orgId
            && i.Status == MembershipStatus.Pending
            && i.DisabledAtUtc is null
            && i.ExpiresAtUtc > DateTimeOffset.UtcNow
        );

        if (invitation is null)
        {
            return Task.FromResult(false);
        }

        invitation.Status = MembershipStatus.Declined;
        invitation.DisabledAtUtc = DateTimeOffset.UtcNow;

        return Task.FromResult(true);
    }
}
