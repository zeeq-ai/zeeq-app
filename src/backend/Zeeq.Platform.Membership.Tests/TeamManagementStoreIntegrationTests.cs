using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Platform.Membership.Tests;

[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TeamManagementStoreIntegrationTests(PgDatabaseFixture postgres)
    : MembershipIntegrationTestBase(postgres)
{
    [Test]
    public async Task Teams_AreAdditiveAndOnlyAcceptActiveMembersOfTheirOrganization()
    {
        var ct = CancellationToken.None;
        await TestBiome
            .Start(_context, out _)
            .AddOrganization(out var first)
            .AddOrganization(out var second)
            .BuildAsync();
        var orgId = first.Organization.Id;
        var owner = first.Organization.CreatedByUserId;
        var outsider = NewId("user");
        await SeedUser(_context, outsider);
        var store = new PostgresTeamManagementStore(_context);

        var engineering = await store.CreateAsync(orgId, owner, "Engineering", null, [owner], ct);
        await Assert.That(engineering is CreateTeamResult.Saved).IsTrue();
        var support = await store.CreateAsync(orgId, owner, "Support", null, [], ct);
        await Assert.That(support is CreateTeamResult.Saved).IsTrue();
        var supportId = ((CreateTeamResult.Saved)support).TeamId;
        var duplicate = await store.CreateAsync(orgId, owner, "engineering", null, [], ct);
        await Assert.That(duplicate is CreateTeamResult.NameTaken).IsTrue();

        var mixed = await store.UpdateAsync(orgId, supportId, owner, [owner, outsider], [], ct);
        await Assert.That(mixed is UpdateTeamResult.IneligibleMember).IsTrue();
        var handler = new TeamManagementHandler(store);
        var overlap = await handler.UpdateAsync(
            orgId,
            supportId,
            TestUser(owner),
            new UpdateTeamMembersRequest([owner, owner], [owner]),
            ct
        );
        await Assert.That(overlap.Result is BadRequest<TeamError>).IsTrue();
        var empty = await store.GetTeamAsync(orgId, supportId, ct);
        await Assert.That(empty!.MemberCount).IsEqualTo(0);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var add = await store.UpdateAsync(orgId, supportId, owner, [owner], [], ct);
            await Assert.That(add is UpdateTeamResult.Saved).IsTrue();
        }

        var roster = await store.ListPeopleAsync(orgId, supportId, false, null, null, ct);
        await Assert.That(roster!.Items.Select(person => person.UserId)).IsEquivalentTo([owner]);
        var candidates = await store.ListPeopleAsync(orgId, supportId, true, null, null, ct);
        await Assert.That(candidates!.Items).IsEmpty();
        var inverse = await store.ListMemberTeamsAsync(orgId, owner, null, ct);
        await Assert
            .That(inverse.Items.Select(team => team.Name))
            .IsEquivalentTo(["Engineering", "Support"]);

        var crossOrg = await store.GetTeamAsync(second.Organization.Id, supportId, ct);
        await Assert.That(crossOrg).IsNull();
        var hideRoot = await store.UpdateAsync(orgId, first.RootTeam.Id, owner, [], [owner], ct);
        await Assert.That(hideRoot is UpdateTeamResult.NotFound).IsTrue();

        await store.UpdateAsync(orgId, supportId, owner, [], [owner], ct);
        inverse = await store.ListMemberTeamsAsync(orgId, owner, null, ct);
        await Assert.That(inverse.Items.Select(team => team.Name)).IsEquivalentTo(["Engineering"]);
    }

    [Test]
    public async Task Create_WithFailedInsert_RestoresTheCallerTransaction()
    {
        var ct = CancellationToken.None;
        await TestBiome.Start(_context, out _).AddOrganization(out var graph).BuildAsync();
        var orgId = graph.Organization.Id;
        var owner = graph.Organization.CreatedByUserId;
        var store = new PostgresTeamManagementStore(_context);

        await Assert
            .That(async () =>
                await store.CreateAsync(orgId, NewId("unknown"), "Support", null, [], ct)
            )
            .Throws<PostgresException>();

        var created = await store.CreateAsync(orgId, owner, "Support", null, [owner], ct);
        await Assert.That(created is CreateTeamResult.Saved).IsTrue();
        var teams = await store.ListTeamsAsync(orgId, null, null, ct);
        await Assert.That(teams.Items.Single().MemberCount).IsEqualTo(1);
    }

    [Test]
    public async Task Directory_PagesByNameAndCountsOnlyTheCurrentTeams()
    {
        var ct = CancellationToken.None;
        await TestBiome.Start(_context, out _).AddOrganization(out var graph).BuildAsync();
        var orgId = graph.Organization.Id;
        var owner = graph.Organization.CreatedByUserId;
        var store = new PostgresTeamManagementStore(_context);
        for (var i = 42; i > 0; i--)
            await store.CreateAsync(orgId, owner, $"Team {i:D2}", null, [owner], ct);

        await store.CreateAsync(orgId, owner, "Team 40é", null, [owner], ct);

        var first = await store.ListTeamsAsync(orgId, null, null, ct);
        await Assert.That(first.Items).Count().IsEqualTo(40);
        await Assert.That(first.Items[0].Name).IsEqualTo("Team 01");
        await Assert.That(first.NextCursor).IsNotNull();
        var last = await store.ListTeamsAsync(orgId, null, first.NextCursor, ct);
        await Assert
            .That(last.Items.Select(team => team.Name))
            .IsEquivalentTo(["Team 40é", "Team 41", "Team 42"]);
        await Assert
            .That(last.Items.Single(team => team.Name == "Team 42").MemberCount)
            .IsEqualTo(1);
        await Assert.That(last.NextCursor).IsNull();

        var search = await store.ListTeamsAsync(orgId, "TEAM 42", null, ct);
        await Assert.That(search.Items.Single().Name).IsEqualTo("Team 42");

        var firstAssignments = await store.ListMemberTeamsAsync(orgId, owner, null, ct);
        await Assert.That(firstAssignments.Items).Count().IsEqualTo(40);
        await Assert.That(firstAssignments.NextCursor).IsEqualTo(firstAssignments.Items[^1].Id);
        var lastAssignments = await store.ListMemberTeamsAsync(
            orgId,
            owner,
            firstAssignments.NextCursor,
            ct
        );
        await Assert.That(lastAssignments.Items).Count().IsEqualTo(3);
        await Assert.That(lastAssignments.NextCursor).IsNull();
        await Assert
            .That(
                firstAssignments
                    .Items.Concat(lastAssignments.Items)
                    .Select(x => x.Id)
                    .Distinct()
                    .Count()
            )
            .IsEqualTo(43);
    }

    [Test]
    public async Task People_PagesCandidatesAndRosterByUserId()
    {
        var ct = CancellationToken.None;
        await TestBiome.Start(_context, out _).AddOrganization(out var graph).BuildAsync();
        var orgId = graph.Organization.Id;
        var owner = graph.Organization.CreatedByUserId;
        var now = DateTimeOffset.UtcNow;
        var ids = new[] { owner }
            .Concat(Enumerable.Range(0, 41).Select(_ => NewId("user")))
            .ToArray();
        foreach (var id in ids.Skip(1))
        {
            _context.Users.Add(
                new User
                {
                    Id = id,
                    DisplayName = id,
                    CreatedAtUtc = now,
                }
            );
            _context.OrganizationMemberships.Add(
                new OrganizationMembership
                {
                    Id = NewId("mem"),
                    OrganizationId = orgId,
                    UserId = id,
                    Role = "member",
                    Status = MembershipStatus.Active,
                    CreatedByUserId = owner,
                    CreatedAtUtc = now,
                }
            );
        }
        await _context.SaveChangesAsync(ct);

        var store = new PostgresTeamManagementStore(_context);
        var created = await store.CreateAsync(orgId, owner, "Engineering", null, [], ct);
        var teamId = ((CreateTeamResult.Saved)created).TeamId;
        var result = await store.UpdateAsync(orgId, teamId, owner, ids, [], ct);
        await Assert.That(result is UpdateTeamResult.Saved).IsTrue();

        foreach (var (scope, candidates) in new (string?, bool)[] { (null, true), (teamId, false) })
        {
            var first = (await store.ListPeopleAsync(orgId, scope, candidates, null, null, ct))!;
            await Assert.That(first.Items).Count().IsEqualTo(40);
            await Assert.That(first.NextCursor).IsEqualTo(first.Items[^1].UserId);
            var last = (
                await store.ListPeopleAsync(orgId, scope, candidates, null, first.NextCursor, ct)
            )!;
            await Assert.That(last.Items).Count().IsEqualTo(2);
            await Assert.That(last.NextCursor).IsNull();
            await Assert
                .That(first.Items.Concat(last.Items).Select(person => person.UserId))
                .IsEquivalentTo(ids);
        }
    }

    [Test]
    public async Task LeavingAndRejoining_RestoresOnlyTheRootTeam()
    {
        var ct = CancellationToken.None;
        await TestBiome.Start(_context, out _).AddOrganization(out var graph).BuildAsync();
        var orgId = graph.Organization.Id;
        var owner = graph.Organization.CreatedByUserId;
        var store = new PostgresTeamManagementStore(_context);
        var memberships = new PostgresZeeqMembershipStore(_context);
        var created = await store.CreateAsync(orgId, owner, "Support", null, [owner], ct);
        await Assert.That(created is CreateTeamResult.Saved).IsTrue();

        await memberships.RemoveMemberAsync(orgId, owner, ct);
        var inverse = await store.ListMemberTeamsAsync(orgId, owner, null, ct);
        await Assert.That(inverse.Items).IsEmpty();

        var invite = await memberships.CreateInvitationAsync(
            new OrganizationMembership
            {
                Id = NewId("membership"),
                OrganizationId = orgId,
                InvitedEmail = "returning@example.com",
                Role = "member",
                Status = MembershipStatus.Pending,
                CreatedByUserId = owner,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            },
            ct
        );
        var accepted = await memberships.AcceptInvitationAsync(invite.Id, owner, ct);
        await Assert.That(accepted).IsTrue();
        var root = await memberships.FindRootTeamIdForMemberAsync(orgId, owner, ct);
        await Assert.That(root).IsEqualTo(graph.RootTeam.Id);

        inverse = await store.ListMemberTeamsAsync(orgId, owner, null, ct);
        await Assert.That(inverse.Items).IsEmpty();
        var disabled = await _context
            .TeamMemberships.AsNoTracking()
            .SingleAsync(
                m =>
                    m.OrganizationId == orgId
                    && m.TeamId == ((CreateTeamResult.Saved)created).TeamId
                    && m.UserId == owner,
                ct
            );
        await Assert.That(disabled.DisabledAtUtc).IsNotNull();
    }
}
