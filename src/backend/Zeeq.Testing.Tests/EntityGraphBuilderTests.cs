using Zeeq.Core.Models;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Testing.Tests;

/// <summary>
/// Integration tests for Zeeq entity graph test scaffolding.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Testing.Tests --output detailed --disable-logo
/// </summary>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class EntityGraphBuilderTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task SeedContext_GenerateWithUserActions_CreatesActiveOrganizationGraph()
    {
        var seed = SeedContext.Generate(
            user => user.Email = "owner@example.test",
            user =>
            {
                user.Email = "member@example.test";
                user.DisplayName = "Member User";
            }
        );

        await Assert.That(seed.Users).Count().IsEqualTo(2);
        await Assert.That(seed.Owner.Email).IsEqualTo("owner@example.test");
        await Assert.That(seed.Organization.CreatedByUserId).IsEqualTo(seed.Owner.Id);
        await Assert.That(seed.RootTeam.OrganizationId).IsEqualTo(seed.Organization.Id);
        await Assert.That(seed.OrganizationMemberships).Count().IsEqualTo(2);
        await Assert.That(seed.TeamMemberships).Count().IsEqualTo(2);
        await Assert.That(seed.OrganizationMemberships[0].Role).IsEqualTo("owner");
        await Assert.That(seed.OrganizationMemberships[1].Role).IsEqualTo("member");
    }

    [Test]
    public async Task AddGeneratedSeed_PersistsSeedGraph()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context, userCount: 2).BuildAsync();

        _context.ChangeTracker.Clear();

        var users = await _context.Users.Where(user => user.Id.StartsWith("user_")).ToArrayAsync();
        var organization = await _context.Organizations.FindAsync(seed.Organization.Id);
        var rootTeam = await _context.Teams.FindAsync(seed.RootTeam.Id);
        var memberships = await _context
            .OrganizationMemberships.Where(membership =>
                membership.OrganizationId == seed.Organization.Id
            )
            .ToArrayAsync();
        var teamMemberships = await _context
            .TeamMemberships.Where(membership => membership.OrganizationId == seed.Organization.Id)
            .ToArrayAsync();

        await Assert.That(users).Count().IsGreaterThanOrEqualTo(2);
        await Assert.That(organization).IsNotNull();
        await Assert.That(rootTeam).IsNotNull();
        await Assert.That(memberships).Count().IsEqualTo(2);
        await Assert.That(teamMemberships).Count().IsEqualTo(2);
    }

    [Test]
    public async Task AddPendingInvitation_PersistsPendingMembership()
    {
        var (seed, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "invited@example.test")
            .BuildAsync();
        var invitation = invitations[0];

        _context.ChangeTracker.Clear();

        var persisted = await _context.OrganizationMemberships.FindAsync(invitation.Id);

        await Assert.That(persisted).IsNotNull();
        await Assert.That(persisted!.OrganizationId).IsEqualTo(seed.Organization.Id);
        await Assert.That(persisted.UserId).IsNull();
        await Assert.That(persisted.Status).IsEqualTo(MembershipStatus.Pending);
        await Assert.That(persisted.InvitedEmail).IsEqualTo("invited@example.test");
        await Assert.That(persisted.ExpiresAtUtc).IsNotNull();
    }

    [Test]
    public async Task AddPendingInvitation_WithMultiplePrototypes_PersistsPendingMemberships()
    {
        var email = "multi-invite@example.test";
        var now = DateTimeOffset.UtcNow;
        var (_, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(
                invitation =>
                {
                    invitation.Email = email;
                    invitation.CreatedAtUtc = now.AddMinutes(-2);
                    invitation.ExpiresAtUtc = now.AddDays(-1);
                },
                invitation =>
                {
                    invitation.Email = email;
                    invitation.Role = "admin";
                    invitation.CreatedAtUtc = now.AddMinutes(-1);
                }
            )
            .BuildAsync();

        _context.ChangeTracker.Clear();

        var invitationIds = invitations.Select(invitation => invitation.Id).ToArray();
        var persisted = await _context
            .OrganizationMemberships.Where(invitation => invitationIds.Contains(invitation.Id))
            .OrderBy(invitation => invitation.CreatedAtUtc)
            .ToArrayAsync();

        await Assert.That(invitations).Count().IsEqualTo(2);
        await Assert.That(persisted).Count().IsEqualTo(2);
        await Assert.That(persisted[0].InvitedEmail).IsEqualTo(email);
        await Assert
            .That(persisted[0].ExpiresAtUtc)
            .IsEqualTo(now.AddDays(-1).TruncateToPostgresPrecision());
        await Assert.That(persisted[1].Role).IsEqualTo("admin");
    }

    [Test]
    public async Task AddPendingInvitation_WithNonPersistentPrototype_ReturnsInvitationWithoutPersisting()
    {
        var (seed, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation =>
            {
                invitation.Email = "create-input@example.test";
                invitation.PersistOnBuild = false;
            })
            .BuildAsync();
        var invitation = invitations[0];

        _context.ChangeTracker.Clear();

        var persistedInvitation = await _context.OrganizationMemberships.FindAsync(invitation.Id);
        var persistedMembershipCount = await _context.OrganizationMemberships.CountAsync(
            membership => membership.OrganizationId == seed.Organization.Id
        );

        await Assert.That(invitation.InvitedEmail).IsEqualTo("create-input@example.test");
        await Assert.That(persistedInvitation).IsNull();
        await Assert.That(persistedMembershipCount).IsEqualTo(1);
    }

    [Test]
    public async Task AddOrganizations_WithPrototypes_PersistsOrganizationGraphsForSeedOwner()
    {
        var (seed, organizationGraphs) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddOrganizations(
                organization =>
                {
                    organization.Slug = "graph-org-a";
                    organization.IsDefaultMembership = true;
                },
                organization =>
                {
                    organization.Slug = "graph-org-b";
                    organization.IsDefaultMembership = false;
                }
            )
            .BuildAsync();

        _context.ChangeTracker.Clear();

        var organizationIds = organizationGraphs.Select(graph => graph.Organization.Id).ToArray();
        var persistedOrganizations = await _context
            .Organizations.Where(organization => organizationIds.Contains(organization.Id))
            .ToArrayAsync();
        var persistedMemberships = await _context
            .OrganizationMemberships.Where(membership =>
                organizationIds.Contains(membership.OrganizationId)
            )
            .ToArrayAsync();
        var membershipA = persistedMemberships.Single(membership =>
            membership.OrganizationId == organizationGraphs[0].Organization.Id
        );
        var membershipB = persistedMemberships.Single(membership =>
            membership.OrganizationId == organizationGraphs[1].Organization.Id
        );

        await Assert.That(organizationGraphs).Count().IsEqualTo(2);
        await Assert.That(persistedOrganizations).Count().IsEqualTo(2);
        await Assert.That(persistedMemberships).Count().IsEqualTo(2);
        await Assert
            .That(persistedMemberships.All(membership => membership.UserId == seed.Owner.Id))
            .IsTrue();
        await Assert.That(membershipA.IsDefault).IsTrue();
        await Assert.That(membershipB.IsDefault).IsFalse();
    }

    [Test]
    public async Task SeedWith_WhenSeedOwnedEntityIsPushed_DoesNotInsertDuplicateSeedRows()
    {
        var seed = SeedContext.Generate();

        var organization = await EntityGraph
            .SeedWith(seed, _context)
            .Add(seedContext => seedContext.Organization)
            .BuildAsync();

        _context.ChangeTracker.Clear();

        var organizationCount = await _context.Organizations.CountAsync(org =>
            org.Id == organization.Id
        );
        var membershipCount = await _context.OrganizationMemberships.CountAsync(membership =>
            membership.OrganizationId == organization.Id
        );

        await Assert.That(organizationCount).IsEqualTo(1);
        await Assert.That(membershipCount).IsEqualTo(1);
    }

    [Test]
    public async Task SeedWith_WhenMixedSeedAndNewEntityArrayIsPushed_PersistsOnlyNewRows()
    {
        var seed = SeedContext.Generate();
        var extraUser = new User
        {
            Id = SeedContext.NewId("user"),
            DisplayName = "Extra User",
            Email = "extra@example.test",
            EmailVerified = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var users = await EntityGraph
            .SeedWith(seed, _context)
            .Push(new[] { seed.Owner, extraUser })
            .BuildAsync();

        _context.ChangeTracker.Clear();

        var userIds = users.Select(user => user.Id).ToArray();
        var persistedUsers = await _context
            .Users.Where(user => userIds.Contains(user.Id))
            .ToArrayAsync();

        await Assert.That(persistedUsers).Count().IsEqualTo(2);
    }

    [Test]
    public async Task AddUsers_WithSetupActions_CreatesOneUserPerAction()
    {
        var (_, users) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddUsers(
                user => user.Email = "alice@example.test",
                user =>
                {
                    user.Email = "bob@example.test";
                    user.DisplayName = "Bob Example";
                }
            )
            .BuildAsync();

        _context.ChangeTracker.Clear();

        var userIds = users.Select(user => user.Id).ToArray();
        var persisted = await _context
            .Users.Where(user => userIds.Contains(user.Id))
            .OrderBy(user => user.Email)
            .ToArrayAsync();

        await Assert.That(users).Count().IsEqualTo(2);
        await Assert.That(persisted).Count().IsEqualTo(2);
        await Assert.That(persisted[0].Email).IsEqualTo("alice@example.test");
        await Assert.That(persisted[1].DisplayName).IsEqualTo("Bob Example");
    }

    [Test]
    public async Task AddUsers_WithCount_CreatesDefaultUsers()
    {
        var (_, users) = await EntityGraph.AddGeneratedSeed(_context).AddUsers(2).BuildAsync();

        _context.ChangeTracker.Clear();

        var userIds = users.Select(user => user.Id).ToArray();
        var persistedCount = await _context.Users.CountAsync(user => userIds.Contains(user.Id));

        await Assert.That(users).Count().IsEqualTo(2);
        await Assert.That(persistedCount).IsEqualTo(2);
        await Assert.That(users.All(user => user.Id.StartsWith("user_"))).IsTrue();
    }
}
