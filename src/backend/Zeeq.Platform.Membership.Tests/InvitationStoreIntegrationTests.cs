using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Postgres integration tests for invitation persistence and handler flows.
///
/// Run all membership tests:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/InvitationStoreIntegrationTests/*"
/// </summary>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class InvitationStoreIntegrationTests(PgDatabaseFixture postgres)
    : MembershipIntegrationTestBase(postgres)
{
    [Test]
    public async Task CreateInvitationAsync_WithValidRequest_ReturnsPendingByEmail()
    {
        // Guards that a created invitation is persisted with a non-null ID and
        // is retrievable by the invited email via ListPendingInvitationsForEmailAsync.
        var store = CreateStore();
        var (_, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation =>
            {
                invitation.Email = "invited@test.com";
                invitation.PersistOnBuild = false;
            })
            .BuildAsync();
        var invitation = invitations[0];

        var inv = await store.CreateInvitationAsync(invitation, CancellationToken.None);

        await Assert.That(inv.Id).IsNotNull();

        var pending = await store.ListPendingInvitationsForEmailAsync(
            "invited@test.com",
            CancellationToken.None
        );

        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Role).IsEqualTo("member");
    }

    [Test]
    public async Task ListPendingInvitationsForEmailAsync_ExcludesExpiredAndDisabledInvitations()
    {
        // Guards that expired invitations (ExpiresAtUtc in the past) and
        // disabled invitations (DisabledAtUtc set) are excluded from the
        // pending list returned to the invited user.
        var store = CreateStore();
        var email = "filter-invites@test.com";
        var now = DateTimeOffset.UtcNow;

        await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(
                invitation =>
                {
                    invitation.Email = email;
                    invitation.CreatedAtUtc = now.AddMinutes(-3);
                    invitation.ExpiresAtUtc = now.AddDays(-1);
                },
                invitation =>
                {
                    invitation.Email = email;
                    invitation.Role = "admin";
                    invitation.CreatedAtUtc = now.AddMinutes(-2);
                    invitation.ExpiresAtUtc = now.AddDays(7);
                    invitation.DisabledAtUtc = now;
                },
                invitation =>
                {
                    invitation.Email = email;
                    invitation.CreatedAtUtc = now.AddMinutes(-1);
                    invitation.ExpiresAtUtc = now.AddDays(7);
                }
            )
            .BuildAsync();

        var pending = await store.ListPendingInvitationsForEmailAsync(
            email,
            CancellationToken.None
        );

        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Role).IsEqualTo("member");
    }

    [Test]
    public async Task AcceptInvitationAsync_WithPendingInvitation_TransitionsToActive()
    {
        // Guards that accepting an invitation sets UserId, transitions Status
        // to Active, and makes the membership visible in ListActiveMemberships.
        var store = CreateStore();
        var (seed, invitations, users) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "join@test.com")
            .AddUsers(user => user.Email = "join@test.com")
            .BuildAsync();
        var invitation = invitations[0];
        var invitedUser = users[0];

        var result = await store.AcceptInvitationAsync(
            invitation.Id,
            invitedUser.Id,
            CancellationToken.None
        );

        await Assert.That(result).IsTrue();

        var memberships = await store.ListActiveMembershipsForUserAsync(
            invitedUser.Id,
            CancellationToken.None
        );

        await Assert.That(memberships).Count().IsEqualTo(1);
        await Assert.That(memberships[0].Status).IsEqualTo(MembershipStatus.Active);

        var rootTeamId = await store.FindRootTeamIdForMemberAsync(
            seed.Organization.Id,
            invitedUser.Id,
            CancellationToken.None
        );
        await Assert.That(rootTeamId).IsEqualTo(seed.RootTeam.Id);
    }

    [Test]
    public async Task AcceptInvitationAsDefaultAsync_WithPendingInvitation_SetsInvitedOrgAsDefault()
    {
        var store = CreateStore();
        var (seed, invitations, users) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "default-join@test.com")
            .AddUsers(user => user.Email = "default-join@test.com")
            .BuildAsync();
        var invitation = invitations[0];
        var invitedUser = users[0];

        var result = await store.AcceptInvitationAsDefaultAsync(
            invitation.Id,
            invitedUser.Id,
            CancellationToken.None
        );

        await Assert.That(result).IsTrue();

        var defaultMemberships = await _context
            .OrganizationMemberships.Where(m => m.UserId == invitedUser.Id && m.IsDefault)
            .ToArrayAsync();

        await Assert.That(defaultMemberships).Count().IsEqualTo(1);
        await Assert.That(defaultMemberships[0].OrganizationId).IsEqualTo(seed.Organization.Id);
    }

    [Test]
    public async Task FindSameDomainInvitationDetailsAsync_WithCallerEmail_ReturnsOwnerAndOrganizationDetails()
    {
        var store = CreateStore();
        var (seed, invitations) = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Acme";
                    organization.IconUrl = "data:image/png;base64,abc";
                    organization.AutoInviteSameDomainEnabled = true;
                    organization.AutoInviteSameDomain = "test.com";
                    organization.AutoInviteDefaultRole = "member";
                }
            )
            .AddPendingInvitation(invitation =>
            {
                invitation.Email = "same-domain@test.com";
                invitation.IsSameDomainAutoInvite = true;
            })
            .BuildAsync();
        seed.Owner.PictureUrl = "https://example.com/owner.png";
        await _context.SaveChangesAsync();
        var invitation = invitations[0];

        var details = await store.FindSameDomainInvitationDetailsAsync(
            invitation.Id,
            "SAME-DOMAIN@test.com",
            CancellationToken.None
        );

        await Assert.That(details).IsNotNull();
        await Assert.That(details!.OrganizationName).IsEqualTo("Acme");
        await Assert.That(details.OrganizationIconUrl).IsEqualTo("data:image/png;base64,abc");
        await Assert.That(details.OwnerUserId).IsEqualTo(seed.Owner.Id);
        await Assert.That(details.OwnerEmail).IsEqualTo(seed.Owner.Email);
        await Assert.That(details.OwnerPictureUrl).IsEqualTo("https://example.com/owner.png");
    }

    [Test]
    public async Task FindSameDomainInvitationDetailsAsync_WithWrongEmail_ReturnsNull()
    {
        var store = CreateStore();
        var (_, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "same-domain@test.com")
            .BuildAsync();

        var details = await store.FindSameDomainInvitationDetailsAsync(
            invitations[0].Id,
            "other@test.com",
            CancellationToken.None
        );

        await Assert.That(details).IsNull();
    }

    [Test]
    public async Task FindSameDomainInvitationDetailsAsync_WithOrdinaryInvitation_ReturnsNull()
    {
        var store = CreateStore();
        var (_, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "ordinary@test.com")
            .BuildAsync();

        var details = await store.FindSameDomainInvitationDetailsAsync(
            invitations[0].Id,
            "ordinary@test.com",
            CancellationToken.None
        );

        await Assert.That(details).IsNull();
    }

    [Test]
    public async Task AcceptInvitationAsync_WithExistingActiveMembership_DeclinesAndReturnsFalse()
    {
        // Guards that accepting an invitation when the user already has an
        // active membership in the same org declines the invitation instead
        // of violating the filtered unique index.
        var store = CreateStore();
        var (seed, inv) = await EntityGraph
            .AddGeneratedSeed(_context, userCount: 2)
            .AddPendingInvitation(invitation =>
            {
                invitation.Email = "existing@test.com";
                invitation.Role = "admin";
            })
            .BuildAsync();
        var invitation = inv[0];
        var member = seed.Users[1];

        var result = await store.AcceptInvitationAsync(
            invitation.Id,
            member.Id,
            CancellationToken.None
        );

        await Assert.That(result).IsFalse();
        await _context.Entry(invitation).ReloadAsync();
        await Assert.That(invitation.Status).IsEqualTo(MembershipStatus.Declined);
        await Assert.That(invitation.DisabledAtUtc).IsNotNull();
    }

    [Test]
    public async Task AcceptInvitationAsync_WithNonExistentId_ReturnsFalse()
    {
        // Guards that accepting a non-existent invitation ID returns false
        // without throwing, so the handler can produce a 404.
        var store = CreateStore();
        var result = await store.AcceptInvitationAsync(
            "nonexistent",
            "test_user",
            CancellationToken.None
        );
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task AcceptInvitationAsync_WithDeclinedInvitation_ReturnsFalse()
    {
        // Guards that an already-declined invitation cannot be re-accepted —
        // the store returns false for non-Pending rows.
        var store = CreateStore();
        var (_, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "declined-accept@test.com")
            .BuildAsync();
        var invitation = invitations[0];

        await store.DeclineInvitationAsync(invitation.Id, CancellationToken.None);

        var result = await store.AcceptInvitationAsync(
            invitation.Id,
            "test_user",
            CancellationToken.None
        );

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task DeclineInvitationAsync_WithPendingInvitation_TransitionsToDeclined()
    {
        // Guards that declining a pending invitation transitions it to Declined
        // status and removes it from the pending list for that email.
        var store = CreateStore();
        var (_, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "decline@test.com")
            .BuildAsync();
        var invitation = invitations[0];

        var result = await store.DeclineInvitationAsync(invitation.Id, CancellationToken.None);

        await Assert.That(result).IsTrue();

        var pending = await store.ListPendingInvitationsForEmailAsync(
            "decline@test.com",
            CancellationToken.None
        );

        await Assert.That(pending).Count().IsEqualTo(0);
    }

    [Test]
    public async Task DeclineInvitationAsync_WithNonExistentId_ReturnsFalse()
    {
        // Guards that declining a non-existent invitation ID returns false
        // without throwing, so the handler can produce a 404.
        var store = CreateStore();
        var result = await store.DeclineInvitationAsync("nonexistent", CancellationToken.None);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CreateInvitationHandler_ValidRequest_CreatesPendingInvitation()
    {
        // Guards the full handler + Postgres store flow for creating a pending membership row.
        // Run this test:
        // dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/*/CreateInvitationHandler_ValidRequest_CreatesPendingInvitation"
        var store = CreateStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(_context, organization => organization.DisplayName = "Test Org")
            .BuildAsync();
        var org = seed.Organization;
        var handler = new CreateInvitationHandler(store);

        var result = await handler.HandleAsync(
            org.Id,
            new CreateInvitationRequest(" invited-integration@test.com ", "admin"),
            TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        var created = result.Result as Created<InvitationResponse>;
        var pending = await store.ListPendingInvitationsForEmailAsync(
            "invited-integration@test.com",
            CancellationToken.None
        );

        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Value!.OrganizationId).IsEqualTo(org.Id);
        await Assert.That(created.Value.OrganizationName).IsEqualTo("Test Org");
        await Assert.That(created.Value.Role).IsEqualTo("admin");
        await Assert.That(pending).Count().IsEqualTo(1);
        await Assert.That(pending[0].Status).IsEqualTo(MembershipStatus.Pending);
        await Assert.That(pending[0].UserId).IsNull();
    }

    [Test]
    public async Task ListInvitationsHandler_WithMixedInvitations_ReturnsOnlyCallerEmail()
    {
        var store = CreateStore();
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                }
            )
            .AddPendingInvitation(invitation => invitation.Email = "current-user@test.com")
            .BuildAsync();
        await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Other Org";
                    organization.Slug = "other-invite-org";
                }
            )
            .AddPendingInvitation(invitation =>
            {
                invitation.Email = "someone-else@test.com";
                invitation.Role = "admin";
            })
            .BuildAsync();

        var org = seed.Organization;

        var handler = new ListInvitationsHandler(store);
        var result = await handler.HandleAsync(
            TestUser("current_user", "current-user@test.com"),
            CancellationToken.None
        );

        await Assert.That(result.Value).IsNotNull();
        await Assert.That(result.Value!).Count().IsEqualTo(1);
        await Assert.That(result.Value![0].OrganizationId).IsEqualTo(org.Id);
        await Assert.That(result.Value![0].OrganizationName).IsEqualTo("Test Org");
    }

    [Test]
    public async Task AcceptInvitationHandler_AddressedInvitation_TransitionsToActive()
    {
        var store = CreateStore();
        var invitedEmail = "accept-integration@test.com";
        var (seed, invitations, users) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = invitedEmail)
            .AddUsers(user => user.Email = invitedEmail)
            .BuildAsync();
        var invitation = invitations[0];
        var invitedUser = users[0];

        var handler = new AcceptInvitationHandler(store);
        var result = await handler.HandleAsync(
            invitation.Id,
            TestUser(invitedUser.Id, invitedEmail),
            CancellationToken.None
        );

        await Assert.That(result.Result is NoContent).IsTrue();

        var activeMemberships = await store.ListActiveMembershipsForUserAsync(
            invitedUser.Id,
            CancellationToken.None
        );
        await _context.Entry(invitation).ReloadAsync();
        await Assert.That(activeMemberships).Count().IsEqualTo(1);
        await Assert.That(activeMemberships[0].OrganizationId).IsEqualTo(seed.Organization.Id);
        await Assert.That(activeMemberships[0].Role).IsEqualTo("member");
        await Assert.That(invitation.Status).IsEqualTo(MembershipStatus.Active);
        await Assert.That(invitation.ExpiresAtUtc).IsNull();

        var rootTeamId = await store.FindRootTeamIdForMemberAsync(
            seed.Organization.Id,
            invitedUser.Id,
            CancellationToken.None
        );
        await Assert.That(rootTeamId).IsEqualTo(seed.RootTeam.Id);
    }

    [Test]
    public async Task DeclineInvitationHandler_WithAddressedInvitation_TransitionsToDeclined()
    {
        var store = CreateStore();
        var (_, invitations) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddPendingInvitation(invitation => invitation.Email = "decline-integration@test.com")
            .BuildAsync();
        var invitation = invitations[0];

        var handler = new DeclineInvitationHandler(store);
        var result = await handler.HandleAsync(
            invitation.Id,
            TestUser("declining_user", "decline-integration@test.com"),
            CancellationToken.None
        );

        await Assert.That(result.Result is NoContent).IsTrue();
        await _context.Entry(invitation).ReloadAsync();
        await Assert.That(invitation.Status).IsEqualTo(MembershipStatus.Declined);
        await Assert.That(invitation.DisabledAtUtc).IsNotNull();
    }
}
