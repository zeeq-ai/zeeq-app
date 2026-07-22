using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Handler unit tests for membership endpoint validation and membership gates.
///
/// Run all membership tests:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/MembershipEndpointHandlerTests/*"
/// </summary>
public sealed class MembershipEndpointHandlerTests
{
    [Test]
    public async Task LeaveOrgHandler_LastOwner_ReturnsValidationProblemAndDoesNotLeave()
    {
        var store = new TestMembershipStore();
        store.Members.Add(
            new OrganizationMember(
                "user_1",
                "Owner",
                "owner@test.com",
                null,
                "owner",
                DateTimeOffset.UtcNow
            )
        );

        // Guards that the last owner in an org cannot leave — the handler
        // returns ValidationProblem and does not call LeaveOrganizationAsync.
        var handler = new LeaveOrgHandler(
            store,
            new TestIdentityStore(),
            NullLogger<LeaveOrgHandler>.Instance
        );
        var result = await handler.HandleAsync(
            "org_target",
            MembershipTestClaims.TestUser("user_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(store.LeaveOrganizationCalls).IsEqualTo(0);
    }

    [Test]
    public async Task LeaveOrgHandler_Success_RevokesOrganizationTokens()
    {
        var store = new TestMembershipStore();
        store.Members.Add(
            new OrganizationMember(
                "user_1",
                "Member",
                "member@test.com",
                null,
                "member",
                DateTimeOffset.UtcNow
            )
        );
        store.Members.Add(
            new OrganizationMember(
                "user_2",
                "Owner",
                "owner@test.com",
                null,
                "owner",
                DateTimeOffset.UtcNow
            )
        );
        var identityStore = new TestIdentityStore();

        // Guards that a successful leave revokes the leaving member's
        // organization-scoped tokens.
        var handler = new LeaveOrgHandler(
            store,
            identityStore,
            NullLogger<LeaveOrgHandler>.Instance
        );
        var result = await handler.HandleAsync(
            "org_target",
            MembershipTestClaims.TestUser("user_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NoContent).IsTrue();
        await Assert.That(identityStore.RevokeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task LeaveOrgHandler_TokenRevocationThrows_StillReturnsNoContent()
    {
        var store = new TestMembershipStore();
        store.Members.Add(
            new OrganizationMember(
                "user_1",
                "Member",
                "member@test.com",
                null,
                "member",
                DateTimeOffset.UtcNow
            )
        );
        store.Members.Add(
            new OrganizationMember(
                "user_2",
                "Owner",
                "owner@test.com",
                null,
                "owner",
                DateTimeOffset.UtcNow
            )
        );
        var identityStore = new TestIdentityStore { ThrowOnRevoke = true };

        // Guards that a throwing token revocation does not prevent the leave
        // itself from succeeding — Change 2's cached membership check is the
        // backstop if this revoke is lost.
        var handler = new LeaveOrgHandler(
            store,
            identityStore,
            NullLogger<LeaveOrgHandler>.Instance
        );
        var result = await handler.HandleAsync(
            "org_target",
            MembershipTestClaims.TestUser("user_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NoContent).IsTrue();
        await Assert.That(store.LeaveOrganizationCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveMemberHandler_Success_RevokesOrganizationTokens()
    {
        var store = new TestMembershipStore();
        store.Memberships.Add(
            new OrganizationMembership
            {
                Id = "membership_1",
                OrganizationId = "org_target",
                UserId = "user_1",
                Role = "member",
                Status = MembershipStatus.Active,
                CreatedByUserId = "user_2",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        var identityStore = new TestIdentityStore();

        // Guards that a successful removal revokes the removed member's
        // organization-scoped tokens.
        var handler = new RemoveMemberHandler(
            store,
            identityStore,
            NullLogger<RemoveMemberHandler>.Instance
        );
        var result = await handler.HandleAsync("org_target", "user_1", CancellationToken.None);

        await Assert.That(result.Result is NoContent).IsTrue();
        await Assert.That(identityStore.RevokeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveMemberHandler_TokenRevocationThrows_StillReturnsNoContent()
    {
        var store = new TestMembershipStore();
        store.Memberships.Add(
            new OrganizationMembership
            {
                Id = "membership_1",
                OrganizationId = "org_target",
                UserId = "user_1",
                Role = "member",
                Status = MembershipStatus.Active,
                CreatedByUserId = "user_2",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        var identityStore = new TestIdentityStore { ThrowOnRevoke = true };

        // Guards that a throwing token revocation does not prevent the
        // removal itself from succeeding.
        var handler = new RemoveMemberHandler(
            store,
            identityStore,
            NullLogger<RemoveMemberHandler>.Instance
        );
        var result = await handler.HandleAsync("org_target", "user_1", CancellationToken.None);

        await Assert.That(result.Result is NoContent).IsTrue();
    }

    [Test]
    public async Task RemoveMemberHandler_NotAMember_ReturnsNotFoundAndDoesNotRevoke()
    {
        var store = new TestMembershipStore();
        var identityStore = new TestIdentityStore();

        var handler = new RemoveMemberHandler(
            store,
            identityStore,
            NullLogger<RemoveMemberHandler>.Instance
        );
        var result = await handler.HandleAsync("org_target", "user_1", CancellationToken.None);

        await Assert.That(result.Result is NotFound).IsTrue();
        await Assert.That(identityStore.RevokeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SetDefaultOrgHandler_MissingMembership_ReturnsNotFound()
    {
        // Guards that setting a default org when the user has no membership
        // returns NotFound without throwing.
        var handler = new SetDefaultOrgHandler(new TestMembershipStore());

        var result = await handler.HandleAsync(
            "org_target",
            MembershipTestClaims.TestUser("user_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    [Test]
    public async Task SwitchOrgHandler_WithRootTeamMembership_UsesTargetRootTeam()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        var store = new TestMembershipStore().AddSeed(seed);

        // Guards that switching organizations writes the target org's root
        // team into the refreshed cookie principal instead of carrying the old team.
        var handler = new SwitchOrgHandler(store, CreateSystemAdminEvaluator());
        var result = await handler.HandleAsync(
            seed.Organization.Id,
            TestUserWithTeam(seed.Owner.Id, "team_old"),
            CancellationToken.None
        );

        var ok = result.Result as Ok<ClaimsPrincipal>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.FindFirstValue(AuthClaims.TeamId)).IsEqualTo(seed.RootTeam.Id);
    }

    [Test]
    public async Task SwitchOrgHandler_WithInactiveOrganization_ReturnsNotFound()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        seed.Organization.ActivatedAtUtc = null;
        var store = new TestMembershipStore().AddSeed(seed);

        // Guards that an active membership row is not enough to switch tenants.
        // The target organization must also be activated, otherwise /me rejects
        // the new cookie and the user can be locked out.
        var handler = new SwitchOrgHandler(store, CreateSystemAdminEvaluator());
        var result = await handler.HandleAsync(
            seed.Organization.Id,
            TestUserWithTeam(seed.Owner.Id, "team_old"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    private static SystemAdminEvaluator CreateSystemAdminEvaluator() =>
        new(new AppSettings { Platform = new PlatformSettings { SystemAdminSubjects = [] } });

    private static ClaimsPrincipal TestUserWithTeam(string userId, string teamId) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(OpenIddictConstants.Claims.Subject, userId),
                    new Claim(OpenIddictConstants.Claims.Email, "user@test.com"),
                    new Claim(AuthClaims.TeamId, teamId),
                ],
                "test"
            )
        );
}
