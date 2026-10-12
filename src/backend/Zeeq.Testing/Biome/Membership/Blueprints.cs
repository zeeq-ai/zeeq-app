using Zeeq.Biome;
using Zeeq.Core.Models;
using Zeeq.Testing.Biomes;

namespace Zeeq.Testing.Biomes;

/// <summary>Membership roots and organization aggregate construction.</summary>
[BiomeGraft]
public static class MembershipBlueprints
{
    /// <summary>Extra users have no automatic membership rows.</summary>
    [Graft("User", Plural = "Users")]
    public static readonly InputBlueprint<SeedContext, UserInput, User> User = Blueprint.FromInput<
        SeedContext,
        UserInput,
        User
    >(
        () => new(),
        (input, ctx) =>
        {
            var id = input.Id ?? ctx.NewId("user");
            var now = input.CreatedAtUtc ?? ctx.Now;
            return new User
            {
                Id = id,
                DisplayName = input.DisplayName ?? $"Graph User {ctx.Index + 1}",
                Email = input.Email ?? $"{id}@example.test",
                EmailVerified = input.EmailVerified,
                CreatedAtUtc = now,
                UpdatedAtUtc = input.UpdatedAtUtc ?? now,
            };
        }
    );

    /// <summary>Pending invitation root with the original seven-day expiry.</summary>
    [Graft("Invitation", Plural = "Invitations")]
    public static readonly InputBlueprint<
        SeedContext,
        PendingInvitationInput,
        OrganizationMembership
    > Invitation = Blueprint.FromInput<SeedContext, PendingInvitationInput, OrganizationMembership>(
        () => new(),
        (prototype, ctx) =>
        {
            var createdAt = prototype.CreatedAtUtc ?? ctx.Now;
            var invitation = new OrganizationMembership
            {
                Id = SeedContext.NewId("mem"),
                OrganizationId = ctx.Seed.Organization.Id,
                UserId = null,
                Role = prototype.Role,
                Status = MembershipStatus.Pending,
                InvitedEmail = prototype.Email ?? $"{SeedContext.NewId("invite")}@example.test",
                IsSameDomainAutoInvite = prototype.IsSameDomainAutoInvite,
                CreatedByUserId = ctx.Seed.Owner.Id,
                CreatedAtUtc = createdAt,
                ExpiresAtUtc = prototype.ExpiresAtUtc ?? createdAt.AddDays(7),
                DisabledAtUtc = prototype.DisabledAtUtc,
            };

            return invitation;
        }
    );

    /// <summary>Organization aggregate persists its four rows in FK order.</summary>
    [Graft("Organization", Plural = "Organizations")]
    public static readonly InputBlueprint<
        SeedContext,
        OrganizationInput,
        OrganizationGraph
    > Organization = Blueprint.FromInput<SeedContext, OrganizationInput, OrganizationGraph>(
        () => new(),
        (prototype, ctx) =>
        {
            var owner = prototype.Owner ?? ctx.Seed.Owner;
            var now = prototype.CreatedAtUtc ?? ctx.Now;
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
                ActivatedAtUtc = prototype.IsActivated ? prototype.ActivatedAtUtc ?? now : null,
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

            return new OrganizationGraph(
                organization,
                rootTeam,
                organizationMembership,
                rootTeamMembership
            );
        }
    );
}
