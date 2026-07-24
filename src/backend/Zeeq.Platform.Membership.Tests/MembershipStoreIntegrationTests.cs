using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Postgres integration tests for membership persistence and member state transitions.
///
/// Run all membership tests:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/MembershipStoreIntegrationTests/*"
/// </summary>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class MembershipStoreIntegrationTests(PgDatabaseFixture postgres)
    : MembershipIntegrationTestBase(postgres)
{
    [Test]
    public async Task CreateOrganizationAsync_WithRootTeamAndMemberships_InsertsGraph()
    {
        // CreateOrganizationAsync starts its own EF transaction. Use a fresh
        // context outside PgTransactionalTestBase's ambient transaction so this
        // test exercises the production transaction path directly.
        await using var context = Postgres.CreateContext();
        var store = new PostgresZeeqMembershipStore(context);
        var userId = NewId("creator");
        await SeedUser(context, userId);
        var suffix = Guid.NewGuid().ToString("N");
        var orgId = "org_" + suffix;
        var teamId = "team_" + suffix;
        var now = DateTimeOffset.UtcNow;

        // Guards that the create-store method inserts the organization, root
        // team, owner org membership, and root team membership atomically.
        await store.CreateOrganizationAsync(
            new Organization
            {
                Id = orgId,
                DisplayName = "Created Org",
                Slug = "created-org-" + suffix[^8..],
                CreatedByUserId = userId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new Team
            {
                Id = teamId,
                OrganizationId = orgId,
                DisplayName = "Created Org Team",
                IsRootTeam = true,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new OrganizationMembership
            {
                Id = "mem_" + suffix,
                OrganizationId = orgId,
                UserId = userId,
                Role = "owner",
                Status = MembershipStatus.Active,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
            },
            new TeamMembership
            {
                OrganizationId = orgId,
                TeamId = teamId,
                UserId = userId,
                Role = "owner",
                CreatedByUserId = userId,
                CreatedAtUtc = now,
            },
            maxCreatedOrganizations: 5,
            CancellationToken.None
        );

        var createdCount = await store.CountOrganizationsCreatedByUserAsync(
            userId,
            CancellationToken.None
        );
        var rootTeamId = await store.FindRootTeamIdForMemberAsync(
            orgId,
            userId,
            CancellationToken.None
        );

        await Assert.That(createdCount).IsEqualTo(1);
        await Assert.That(rootTeamId).IsEqualTo(teamId);
    }

    [Test]
    public async Task ListActiveMembershipsForUserAsync_WithActiveMembership_ReturnsMembershipWithRole()
    {
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        // Guards that only memberships with Status=Active and no DisabledAtUtc
        // are returned for the target user, and that the role and org ID match.

        var list = await store.ListActiveMembershipsForUserAsync(
            seed.Owner.Id,
            CancellationToken.None
        );

        await Assert.That(list).Count().IsEqualTo(1);
        await Assert.That(list[0].Role).IsEqualTo("owner");
        await Assert.That(list[0].OrganizationId).IsEqualTo(seed.Organization.Id);
    }

    [Test]
    public async Task ListActiveMembershipsForUserAsync_WithDisabledOrg_ExcludesMembership()
    {
        // Guards that disabled organizations are excluded from active membership
        // queries even when the membership row itself is still Active.
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        seed.Organization.DisabledAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        var list = await store.ListActiveMembershipsForUserAsync(
            seed.Owner.Id,
            CancellationToken.None
        );

        await Assert.That(list).Count().IsEqualTo(0);
    }

    [Test]
    public async Task ListActiveMembershipsForUserAsync_WithDifferentUser_ReturnsEmpty()
    {
        // Guards that a user cannot see another user's memberships — the query
        // is scoped to the requesting userId.
        var store = CreateStore();
        await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        var list = await store.ListActiveMembershipsForUserAsync(
            NewId("user"),
            CancellationToken.None
        );

        await Assert.That(list).Count().IsEqualTo(0);
    }

    [Test]
    public async Task ListActiveMembershipsForUserAsync_WithDisabledMembership_ReturnsEmpty()
    {
        // Guards that disabled/removed membership rows (Status != Active or
        // DisabledAtUtc set) are excluded from active membership queries.
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var membership = seed.OrganizationMemberships[0];

        membership.Status = MembershipStatus.Disabled;
        membership.DisabledAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        var list = await store.ListActiveMembershipsForUserAsync(
            seed.Owner.Id,
            CancellationToken.None
        );

        await Assert.That(list).Count().IsEqualTo(0);
    }

    [Test]
    public async Task ListMembersForOrganizationAsync_WithPendingInvitation_ExcludesFromMemberList()
    {
        // Guards that pending invitations (Status=Pending, UserId=null) are
        // excluded from the member list — only active members with a user row appear.
        var store = CreateStore();
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context, userCount: 2)
            .AddPendingInvitation(invitation => invitation.Email = "pending@test.com")
            .BuildAsync();

        var members = await store.ListMembersForOrganizationAsync(
            seed.Organization.Id,
            CancellationToken.None
        );

        await Assert.That(members).Count().IsEqualTo(2);
        await Assert.That(members.Select(m => m.UserId)).Contains(seed.Users[0].Id);
        await Assert.That(members.Select(m => m.UserId)).Contains(seed.Users[1].Id);
    }

    [Test]
    public async Task SetDefaultOrganizationAsync_WithExistingMemberships_UpdatesDefaultAndClearsOld()
    {
        // SetDefaultOrganizationAsync starts its own EF transaction. Use a fresh
        // context outside PgTransactionalTestBase's ambient transaction so this
        // test exercises the production transaction path directly.
        // Run this test:
        // dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/*/SetDefaultOrganizationAsync_WithExistingMemberships_UpdatesDefaultAndClearsOld"
        await using var context = Postgres.CreateContext();
        var store = new PostgresZeeqMembershipStore(context);
        var (seed, organizationGraphs) = await EntityGraph
            .AddGeneratedSeed(context)
            .AddOrganizations(
                org =>
                {
                    org.Slug = NewId("org-a-def");
                    org.IsDefaultMembership = true;
                },
                org =>
                {
                    org.Slug = NewId("org-b-def");
                    org.IsDefaultMembership = false;
                }
            )
            .BuildAsync();
        var orgA = organizationGraphs[0].Organization;
        var orgB = organizationGraphs[1].Organization;

        await store.SetDefaultOrganizationAsync(seed.Owner.Id, orgB.Id, CancellationToken.None);

        var memberships = await store.ListActiveMembershipsForUserAsync(
            seed.Owner.Id,
            CancellationToken.None
        );
        var a = memberships.First(m => m.OrganizationId == orgA.Id);
        var b = memberships.First(m => m.OrganizationId == orgB.Id);

        await Assert.That(a.IsDefault).IsFalse();
        await Assert.That(b.IsDefault).IsTrue();
    }

    [Test]
    public async Task SetDefaultOrganizationAsync_WithMissingMembership_ThrowsInvalidOperation()
    {
        // Guards the rollback path where the target active membership vanishes
        // or never existed. This must call the store method, not direct EF
        // updates, because the transaction behavior is the invariant under test.
        // Run this test:
        // dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/*/SetDefaultOrganizationAsync_WithMissingMembership_ThrowsInvalidOperation"
        await using var context = Postgres.CreateContext();
        var store = new PostgresZeeqMembershipStore(context);

        var threw = false;
        try
        {
            await store.SetDefaultOrganizationAsync(
                NewId("user"),
                NewId("org"),
                CancellationToken.None
            );
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task LeaveOrganizationAsync_WithActiveMembership_DisablesAndClearsDefault()
    {
        // Guards that self-service leave disables the membership row and clears
        // the IsDefault flag, removing the user from the active org list.
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var membership = seed.OrganizationMemberships[0];

        await store.LeaveOrganizationAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            CancellationToken.None
        );

        var list = await store.ListActiveMembershipsForUserAsync(
            seed.Owner.Id,
            CancellationToken.None
        );
        await Assert.That(list).Count().IsEqualTo(0);
        await _context.Entry(membership).ReloadAsync();
        await Assert.That(membership.Status).IsEqualTo(MembershipStatus.Disabled);
        await Assert.That(membership.DisabledAtUtc).IsNotNull();
        await Assert.That(membership.IsDefault).IsFalse();
    }

    [Test]
    public async Task RemoveMemberAsync_WithActiveMembership_DisablesAndClearsDefault()
    {
        // Guards that admin-initiated removal disables the membership row,
        // clears IsDefault, and preserves audit history (row persists).
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var membership = seed.OrganizationMemberships[0];

        await store.RemoveMemberAsync(seed.Organization.Id, seed.Owner.Id, CancellationToken.None);

        var list = await store.ListActiveMembershipsForUserAsync(
            seed.Owner.Id,
            CancellationToken.None
        );
        await Assert.That(list).Count().IsEqualTo(0);
        await _context.Entry(membership).ReloadAsync();
        await Assert.That(membership.Status).IsEqualTo(MembershipStatus.Disabled);
        await Assert.That(membership.DisabledAtUtc).IsNotNull();
        await Assert.That(membership.IsDefault).IsFalse();
    }

    [Test]
    public async Task RemoveMemberAsync_WithUserAlias_PreservesAlias()
    {
        // Org-scoped aliases are attribution keys for historical PRs and
        // telemetry, so removing a member must not delete or disable aliases.
        var store = CreateStore();
        var (seed, aliases) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddUserAliases(alias =>
            {
                alias.Kind = UserAliasKind.GitHub;
                alias.DisplayValue = "CharlieDigital";
                alias.NormalizedValue = "charliedigital";
            })
            .BuildAsync();
        var alias = aliases[0];

        await store.RemoveMemberAsync(seed.Organization.Id, seed.Owner.Id, CancellationToken.None);

        var persisted = await _context.UserAliases.SingleAsync(
            row => row.Id == alias.Id,
            CancellationToken.None
        );

        await Assert.That(persisted.OrganizationId).IsEqualTo(seed.Organization.Id);
        await Assert.That(persisted.UserId).IsEqualTo(seed.Owner.Id);
        await Assert.That(persisted.DisabledAtUtc).IsNull();
    }

    [Test]
    public async Task UpdateMemberRoleAsync_WithExistingMember_UpdatesRoleToAdmin()
    {
        // Guards that role changes are persisted atomically via ExecuteUpdateAsync
        // and are immediately visible to subsequent queries.
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context, userCount: 2).BuildAsync();
        var member = seed.Users[1];
        var memberMembership = seed.OrganizationMemberships[1];

        await Assert.That(memberMembership.UserId).IsEqualTo(member.Id);
        await Assert.That(memberMembership.Role).IsEqualTo("member");

        await store.UpdateMemberRoleAsync(
            seed.Organization.Id,
            member.Id,
            "admin",
            CancellationToken.None
        );

        var list = await store.ListActiveMembershipsForUserAsync(member.Id, CancellationToken.None);
        await Assert.That(list).Count().IsEqualTo(1);
        await Assert.That(list[0].Role).IsEqualTo("admin");
    }

    [Test]
    public async Task UpdateMemberRoleAsync_WithMissingMember_DoesNotAffectOtherRows()
    {
        // Guards that updating a non-existent user's role is a no-op and does
        // not affect other members in the same organization.
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context, userCount: 2).BuildAsync();
        var member = seed.Users[1];
        var memberMembership = seed.OrganizationMemberships[1];

        await Assert.That(memberMembership.UserId).IsEqualTo(member.Id);
        await Assert.That(memberMembership.Role).IsEqualTo("member");

        await store.UpdateMemberRoleAsync(
            seed.Organization.Id,
            NewId("user"),
            "admin",
            CancellationToken.None
        );

        var list = await store.ListActiveMembershipsForUserAsync(member.Id, CancellationToken.None);
        await Assert.That(list).Count().IsEqualTo(1);
        await Assert.That(list[0].Role).IsEqualTo("member");
    }

    [Test]
    public async Task ActiveMembershipUniqueIndex_WithDuplicateActive_ThrowsDbUpdateException()
    {
        // Guards that the filtered unique index on (organization_id, user_id)
        // for active memberships prevents duplicate rows at the database level.
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        _context.OrganizationMemberships.Add(
            new OrganizationMembership
            {
                Id = NewId("mem"),
                OrganizationId = seed.Organization.Id,
                UserId = seed.Owner.Id,
                Role = "admin",
                Status = MembershipStatus.Active,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );

        var saveFailed = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            saveFailed = true;
        }

        await Assert.That(saveFailed).IsTrue();
    }

    [Test]
    public async Task FindMembershipActivationStateAsync_WithActiveMembership_ReturnsActiveState()
    {
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        var state = await store.FindMembershipActivationStateAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            CancellationToken.None
        );

        // Guards that an active, non-disabled membership row reports IsActive.
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.IsActive).IsTrue();
    }

    [Test]
    public async Task FindMembershipActivationStateAsync_WithDisabledMembership_ReturnsInactiveState()
    {
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        await store.RemoveMemberAsync(seed.Organization.Id, seed.Owner.Id, CancellationToken.None);

        var state = await store.FindMembershipActivationStateAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            CancellationToken.None
        );

        // Guards that a disabled row is still returned (so the token
        // middleware can distinguish "missing" from "disabled") but reports
        // IsActive = false.
        await Assert.That(state).IsNotNull();
        await Assert.That(state!.IsActive).IsFalse();
    }

    [Test]
    public async Task FindMembershipActivationStateAsync_WithHistoricalAndCurrentMembership_ReturnsCurrentActiveState()
    {
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        // Simulate: the user was removed (row soft-deleted, retained for
        // audit history) and later re-invited/re-added to the same
        // organization — a legal state where (OrganizationId, UserId) is no
        // longer unique across all rows, only across active ones.
        await store.RemoveMemberAsync(seed.Organization.Id, seed.Owner.Id, CancellationToken.None);
        _context.OrganizationMemberships.Add(
            new OrganizationMembership
            {
                Id = NewId("mem"),
                OrganizationId = seed.Organization.Id,
                UserId = seed.Owner.Id,
                Role = "member",
                Status = MembershipStatus.Active,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        await _context.SaveChangesAsync();

        // Guards that FindMembershipActivationStateAsync tolerates more
        // than one row for the pair and prefers the current active
        // membership instead of throwing (previously used
        // SingleOrDefaultAsync, which would fail here).
        var state = await store.FindMembershipActivationStateAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            CancellationToken.None
        );

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.IsActive).IsTrue();
    }

    [Test]
    public async Task FindMembershipActivationStateAsync_WithNoMembershipRow_ReturnsNull()
    {
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        var state = await store.FindMembershipActivationStateAsync(
            seed.Organization.Id,
            "user_never_a_member",
            CancellationToken.None
        );

        await Assert.That(state).IsNull();
    }
}
