using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Models;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Handler unit tests for invitation endpoint validation and ownership gates.
///
/// Run all membership tests:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/InvitationEndpointHandlerTests/*"
/// </summary>
public sealed class InvitationEndpointHandlerTests
{
    [Test]
    public async Task CreateInvitationHandler_DuplicateInvitation_ReturnsValidationProblem()
    {
        var (seed, invitations) = await EntityGraph
            .AddGeneratedSeed()
            .AddPendingInvitation(invitation => invitation.Email = "invited@test.com")
            .BuildAsync();
        var store = new TestMembershipStore().AddSeed(seed).AddInvitations(invitations);

        // Guards that attempting to invite an email that already has a pending
        // invitation for the same org returns ValidationProblem and does not
        // create a duplicate row.
        var handler = new CreateInvitationHandler(store);
        var result = await handler.HandleAsync(
            seed.Organization.Id,
            new CreateInvitationRequest(" invited@test.com ", "member"),
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(store.Invitations).Count().IsEqualTo(1);
    }

    [Test]
    public async Task AcceptInvitationHandler_WrongEmail_ReturnsNotFoundAndDoesNotAccept()
    {
        var (seed, invitations) = await EntityGraph
            .AddGeneratedSeed()
            .AddPendingInvitation(invitation => invitation.Email = "other@test.com")
            .BuildAsync();
        var invitation = invitations[0];
        var store = new TestMembershipStore().AddSeed(seed).AddInvitations(invitations);

        // Guards that a user cannot accept an invitation addressed to a
        // different email — the ownership gate returns NotFound and the
        // invitation row is unchanged.
        var handler = new AcceptInvitationHandler(store);
        var result = await handler.HandleAsync(
            invitation.Id,
            MembershipTestClaims.TestUser(seed.Owner.Id, "caller@test.com"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
        await Assert.That(invitation.Status).IsEqualTo(MembershipStatus.Pending);
        await Assert.That(invitation.UserId).IsNull();
    }

    [Test]
    public async Task AcceptInvitationAsDefaultHandler_WithManualInvitation_AcceptsInvitation()
    {
        var (seed, invitations) = await EntityGraph
            .AddGeneratedSeed()
            .AddPendingInvitation(invitation => invitation.Email = "manual@test.com")
            .BuildAsync();
        var invitation = invitations[0];
        var store = new TestMembershipStore().AddSeed(seed).AddInvitations(invitations);

        var handler = new AcceptInvitationAsDefaultHandler(store);
        var result = await handler.HandleAsync(
            invitation.Id,
            MembershipTestClaims.TestUser("usr_manual", "manual@test.com"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NoContent).IsTrue();
        var acceptedMembership = store.Memberships.Single(membership =>
            membership.Id == invitation.Id
        );
        await Assert.That(acceptedMembership.Status).IsEqualTo(MembershipStatus.Active);
        await Assert.That(acceptedMembership.UserId).IsEqualTo("usr_manual");
        await Assert.That(acceptedMembership.IsDefault).IsTrue();
    }
}
