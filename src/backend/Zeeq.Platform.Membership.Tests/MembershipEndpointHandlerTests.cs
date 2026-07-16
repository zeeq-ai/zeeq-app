using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Testing.EntityGraphs;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;

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
        var handler = new LeaveOrgHandler(store);
        var result = await handler.HandleAsync(
            "org_target",
            MembershipTestClaims.TestUser("user_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(store.LeaveOrganizationCalls).IsEqualTo(0);
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
