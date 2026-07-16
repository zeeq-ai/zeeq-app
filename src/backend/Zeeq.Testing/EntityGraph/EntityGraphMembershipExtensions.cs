using Zeeq.Core.Models;

namespace Zeeq.Testing.EntityGraphs;

/// <summary>
/// Test-builder prototype for creating pending invitation membership rows.
/// </summary>
public sealed class PendingInvitationPrototype
{
    /// <summary>
    /// Invited email address.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Membership role granted when the invitation is accepted.
    /// </summary>
    public string Role { get; set; } = "member";

    /// <summary>
    /// Created timestamp for the generated invitation.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Expiration timestamp for the generated invitation.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Disabled timestamp for the generated invitation.
    /// </summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }

    /// <summary>
    /// Whether the generated invitation should be persisted when the graph is built.
    /// </summary>
    public bool PersistOnBuild { get; set; } = true;
}

/// <summary>
/// Test-builder prototype for creating organization membership graphs.
/// </summary>
public sealed class OrganizationPrototype
{
    /// <summary>
    /// Organization display name.
    /// </summary>
    public string DisplayName { get; set; } = "Test Organization";

    /// <summary>
    /// Organization slug. A generated slug is used when omitted.
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>
    /// User that owns and belongs to the generated organization.
    /// </summary>
    public User? Owner { get; set; }

    /// <summary>
    /// Role assigned to the owner membership.
    /// </summary>
    public string Role { get; set; } = "owner";

    /// <summary>
    /// Whether the generated owner membership is the user's default organization.
    /// </summary>
    public bool IsDefaultMembership { get; set; }

    /// <summary>
    /// Created timestamp for generated rows.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Activation timestamp for the generated organization.
    /// </summary>
    public DateTimeOffset? ActivatedAtUtc { get; set; }
}

/// <summary>
/// Organization graph returned by <see cref="EntityGraphMembershipExtensions"/>.
/// </summary>
/// <param name="Organization">Generated organization.</param>
/// <param name="RootTeam">Generated root team.</param>
/// <param name="OrganizationMembership">Generated owner organization membership.</param>
/// <param name="RootTeamMembership">Generated owner root-team membership.</param>
public sealed record OrganizationGraph(
    Organization Organization,
    Team RootTeam,
    OrganizationMembership OrganizationMembership,
    TeamMembership RootTeamMembership
)
{
    /// <summary>
    /// Returns generated rows in foreign-key-safe insertion order.
    /// </summary>
    /// <returns>Rows that make up the organization graph.</returns>
    public IReadOnlyList<object> GetEntities() =>
        [Organization, RootTeam, OrganizationMembership, RootTeamMembership];
}

/// <summary>
/// Membership-specific entity graph helpers.
/// </summary>
public static class EntityGraphMembershipExtensions
{
    extension<TState>(EntityGraphBuilder<TState> builder)
    {
        /// <summary>
        /// Adds one user per setup action.
        /// </summary>
        /// <param name="userSetupActions">Per-user setup actions. Empty creates one default user.</param>
        /// <returns>A builder with the created users in the result tuple.</returns>
        public EntityGraphBuilder<(TState Previous, User[] Users)> AddUsers(
            params Action<User>[] userSetupActions
        ) => builder.AddMany(CreateUser, userSetupActions);

        /// <summary>
        /// Adds the requested number of default users.
        /// </summary>
        /// <param name="count">Number of users to create.</param>
        /// <returns>A builder with the created users in the result tuple.</returns>
        public EntityGraphBuilder<(TState Previous, User[] Users)> AddUsers(int count) =>
            builder.AddMany(CreateUser, count);

        /// <summary>
        /// Adds the requested number of default organization membership graphs.
        /// </summary>
        /// <param name="count">Number of organization graphs to create.</param>
        /// <returns>A builder with the organization graphs in the result tuple.</returns>
        public EntityGraphBuilder<(
            TState Previous,
            OrganizationGraph[] OrganizationGraphs
        )> AddOrganizations(int count) =>
            builder.AddOrganizations([
                .. Enumerable
                    .Range(0, count)
                    .Select(_ => (Action<OrganizationPrototype>)(_ => { })),
            ]);

        /// <summary>
        /// Adds one organization membership graph per prototype customization action.
        /// </summary>
        /// <param name="customize">
        /// Function supplied at the call site to customize each organization prototype.
        /// Empty creates one default organization graph.
        /// </param>
        /// <returns>A builder with the organization graphs in the result tuple.</returns>
        public EntityGraphBuilder<(
            TState Previous,
            OrganizationGraph[] OrganizationGraphs
        )> AddOrganizations(params Action<OrganizationPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var organizationGraphs = new OrganizationGraph[customize.Length];

            for (var index = 0; index < customize.Length; index++)
            {
                var prototype = new OrganizationPrototype();
                customize[index].Invoke(prototype);

                var owner = prototype.Owner ?? builder.Seed.Owner;
                var now = prototype.CreatedAtUtc ?? DateTimeOffset.UtcNow;
                var organizationId = SeedContext.NewId("org");
                var slugSuffix = organizationId[^8..];
                var teamId = SeedContext.NewId("team");

                var organization = new Organization
                {
                    Id = organizationId,
                    DisplayName = prototype.DisplayName,
                    Slug = prototype.Slug ?? $"test-organization-{slugSuffix}",
                    CreatedByUserId = owner.Id,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    ActivatedAtUtc = prototype.ActivatedAtUtc ?? now,
                };

                var rootTeam = new Team
                {
                    Id = teamId,
                    OrganizationId = organization.Id,
                    DisplayName = "Root Team",
                    IsRootTeam = true,
                    CreatedByUserId = owner.Id,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };

                var organizationMembership = new OrganizationMembership
                {
                    Id = SeedContext.NewId("mem"),
                    OrganizationId = organization.Id,
                    UserId = owner.Id,
                    Role = prototype.Role,
                    Status = MembershipStatus.Active,
                    IsDefault = prototype.IsDefaultMembership,
                    CreatedByUserId = owner.Id,
                    CreatedAtUtc = now,
                };

                var rootTeamMembership = new TeamMembership
                {
                    OrganizationId = organization.Id,
                    TeamId = rootTeam.Id,
                    UserId = owner.Id,
                    Role = prototype.Role,
                    CreatedByUserId = owner.Id,
                    CreatedAtUtc = now,
                };

                organizationGraphs[index] = new OrganizationGraph(
                    organization,
                    rootTeam,
                    organizationMembership,
                    rootTeamMembership
                );
            }

            return builder.Push(organizationGraphs);
        }

        /// <summary>
        /// Adds the requested number of default pending organization invitations.
        /// </summary>
        /// <param name="count">Number of invitations to create.</param>
        /// <returns>A builder with the invitations in the result tuple.</returns>
        public EntityGraphBuilder<(
            TState Previous,
            OrganizationMembership[] Invitations
        )> AddPendingInvitation(int count) =>
            builder.AddPendingInvitation([
                .. Enumerable
                    .Range(0, count)
                    .Select(_ => (Action<PendingInvitationPrototype>)(_ => { })),
            ]);

        /// <summary>
        /// Adds one pending organization invitation per prototype customization action.
        /// </summary>
        /// <param name="customize">
        /// Function supplied at the call site to customize each invitation prototype.
        /// Empty creates one default invitation.
        /// </param>
        /// <returns>A builder with the invitations in the result tuple.</returns>
        public EntityGraphBuilder<(
            TState Previous,
            OrganizationMembership[] Invitations
        )> AddPendingInvitation(params Action<PendingInvitationPrototype>[] customize)
        {
            if (customize.Length == 0)
            {
                customize = [_ => { }];
            }

            var now = DateTimeOffset.UtcNow;
            var invitations = new OrganizationMembership[customize.Length];
            var nonPersistentInvitations = new List<object>();

            for (var index = 0; index < customize.Length; index++)
            {
                var prototype = new PendingInvitationPrototype();
                customize[index].Invoke(prototype);

                var createdAt = prototype.CreatedAtUtc ?? now;
                var invitation = new OrganizationMembership
                {
                    Id = SeedContext.NewId("mem"),
                    OrganizationId = builder.Seed.Organization.Id,
                    UserId = null,
                    Role = prototype.Role,
                    Status = MembershipStatus.Pending,
                    InvitedEmail = prototype.Email ?? $"{SeedContext.NewId("invite")}@example.test",
                    CreatedByUserId = builder.Seed.Owner.Id,
                    CreatedAtUtc = createdAt,
                    ExpiresAtUtc = prototype.ExpiresAtUtc ?? createdAt.AddDays(7),
                    DisabledAtUtc = prototype.DisabledAtUtc,
                };

                invitations[index] = invitation;

                if (!prototype.PersistOnBuild)
                {
                    nonPersistentInvitations.Add(invitation);
                }
            }

            return builder.Push(invitations, nonPersistentEntities: nonPersistentInvitations);
        }
    }

    private static User CreateUser(SeedContext seed, int index)
    {
        var userId = SeedContext.NewId("user");
        var now = DateTimeOffset.UtcNow;

        return new User
        {
            Id = userId,
            DisplayName = $"Graph User {index + 1}",
            Email = $"{userId}@example.test",
            EmailVerified = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }
}
