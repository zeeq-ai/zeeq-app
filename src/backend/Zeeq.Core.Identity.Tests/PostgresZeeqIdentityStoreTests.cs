using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Integration tests for the Postgres identity store user/bootstrap flow.
/// </summary>
/// <param name="postgres">Shared Postgres fixture for transactional tests.</param>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresZeeqIdentityStoreTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    /// <summary>
    /// Verifies that new-user bootstrap creates an organization slug with the ID suffix.
    /// </summary>
    [Test]
    public async Task EnsureUserAsync_NewUser_CreatesOrganizationSlugWithIdSuffix()
    {
        var context = await new PostgresZeeqIdentityStore(_context).EnsureUserAsync(
            "mock",
            Guid.NewGuid().ToString("N"),
            "My Org",
            null,
            null,
            CancellationToken.None
        );

        var organization = await _context.Organizations.SingleAsync(org =>
            org.Id == context.OrganizationId
        );

        // Guards that first-login organizations get a stable URL-safe slug while
        // avoiding collisions by suffixing the generated organization id.
        await Assert.That(organization.Slug).IsEqualTo($"my-org-{context.OrganizationId[^8..]}");
    }

    [Test]
    public async Task EnsureUserAsync_NewMatchingPrivateDomain_CreatesPendingSameDomainInvitation()
    {
        await SeedSameDomainOrganizationAsync("example.com", "admin");

        var context = await new PostgresZeeqIdentityStore(_context).EnsureUserAsync(
            "mock",
            Guid.NewGuid().ToString("N"),
            "Domain User",
            "new-user@example.com",
            null,
            CancellationToken.None
        );

        var invitations = await _context
            .OrganizationMemberships.Where(m =>
                m.InvitedEmail == "new-user@example.com" && m.Status == MembershipStatus.Pending
            )
            .ToArrayAsync();

        await Assert.That(invitations).Count().IsEqualTo(1);
        await Assert.That(invitations[0].OrganizationId).IsNotEqualTo(context.OrganizationId);
        await Assert.That(invitations[0].Role).IsEqualTo("admin");
        await Assert.That(invitations[0].IsSameDomainAutoInvite).IsTrue();
        await Assert.That(invitations[0].ExpiresAtUtc).IsNotNull();
    }

    [Test]
    public async Task EnsureUserAsync_NewMatchingPrivateDomain_ReplacesExpiredSameDomainInvitation()
    {
        var target = await SeedSameDomainOrganizationAsync("example.com", "member");
        var expiredInvitation = new OrganizationMembership
        {
            Id = "mem_" + Guid.NewGuid().ToString("N"),
            OrganizationId = target.OrganizationId,
            UserId = null,
            Role = "member",
            Status = MembershipStatus.Pending,
            InvitedEmail = "expired-user@example.com",
            IsSameDomainAutoInvite = true,
            CreatedByUserId = target.OwnerId,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        };
        _context.OrganizationMemberships.Add(expiredInvitation);
        await _context.SaveChangesAsync();

        await new PostgresZeeqIdentityStore(_context).EnsureUserAsync(
            "mock",
            Guid.NewGuid().ToString("N"),
            "Domain User",
            "expired-user@example.com",
            null,
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var invitations = await _context
            .OrganizationMemberships.Where(m => m.InvitedEmail == "expired-user@example.com")
            .OrderBy(m => m.CreatedAtUtc)
            .ToArrayAsync();

        await Assert.That(invitations).Count().IsEqualTo(2);
        await Assert.That(invitations[0].Status).IsEqualTo(MembershipStatus.Declined);
        await Assert.That(invitations[0].DisabledAtUtc).IsNotNull();
        await Assert.That(invitations[1].Status).IsEqualTo(MembershipStatus.Pending);
        await Assert.That(invitations[1].DisabledAtUtc).IsNull();
        await Assert.That(invitations[1].ExpiresAtUtc).IsNotNull();
        await Assert.That(invitations[1].ExpiresAtUtc!.Value).IsGreaterThan(DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task EnsureUserAsync_ExistingIdentity_DoesNotCreateAdditionalSameDomainInvitation()
    {
        await SeedSameDomainOrganizationAsync("example.com", "member");
        var providerSubject = Guid.NewGuid().ToString("N");
        var store = new PostgresZeeqIdentityStore(_context);

        await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "existing-user@example.com",
            null,
            CancellationToken.None
        );
        var user = await _context.Users.SingleAsync(user =>
            user.Email == "existing-user@example.com"
        );
        await AddActivatedOrganizationMembershipAsync(
            user.Id,
            "Existing User Activated Org",
            isDefault: false,
            createdAtUtc: DateTimeOffset.UtcNow
        );
        await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "existing-user@example.com",
            null,
            CancellationToken.None
        );

        var invitationCount = await _context.OrganizationMemberships.CountAsync(m =>
            m.InvitedEmail == "existing-user@example.com" && m.Status == MembershipStatus.Pending
        );

        await Assert.That(invitationCount).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUserAsync_ExistingIdentityWithDeclinedInvitation_DoesNotRecreateSameDomainInvitation()
    {
        await SeedSameDomainOrganizationAsync("example.com", "member");
        var providerSubject = Guid.NewGuid().ToString("N");
        var store = new PostgresZeeqIdentityStore(_context);

        await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "declined-user@example.com",
            null,
            CancellationToken.None
        );

        var invitation = await _context.OrganizationMemberships.SingleAsync(m =>
            m.InvitedEmail == "declined-user@example.com" && m.Status == MembershipStatus.Pending
        );
        invitation.Status = MembershipStatus.Declined;
        await _context.SaveChangesAsync();
        var user = await _context.Users.SingleAsync(user =>
            user.Email == "declined-user@example.com"
        );
        await AddActivatedOrganizationMembershipAsync(
            user.Id,
            "Declined User Activated Org",
            isDefault: false,
            createdAtUtc: DateTimeOffset.UtcNow
        );

        await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "declined-user@example.com",
            null,
            CancellationToken.None
        );

        var invitationCount = await _context.OrganizationMemberships.CountAsync(m =>
            m.InvitedEmail == "declined-user@example.com"
        );
        var pendingInvitationCount = await _context.OrganizationMemberships.CountAsync(m =>
            m.InvitedEmail == "declined-user@example.com" && m.Status == MembershipStatus.Pending
        );

        await Assert.That(invitationCount).IsEqualTo(1);
        await Assert.That(pendingInvitationCount).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureUserAsync_ExistingIdentityWithOnlyInactiveOrganizations_DoesNotReturnInactiveContext()
    {
        var providerSubject = Guid.NewGuid().ToString("N");
        var store = new PostgresZeeqIdentityStore(_context);

        await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Inactive User",
            "inactive-user@example.com",
            null,
            CancellationToken.None
        );

        Func<Task> act = async () =>
            await store.EnsureUserAsync(
                "mock",
                providerSubject,
                "Inactive User",
                "inactive-user@example.com",
                null,
                CancellationToken.None
            );

        await Assert.That(act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EnsureUserAsync_ExistingIdentityWithInactiveOrgMembership_DoesNotUseStaleTeamMembership()
    {
        var providerSubject = Guid.NewGuid().ToString("N");
        var store = new PostgresZeeqIdentityStore(_context);
        await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Stale Team User",
            "stale-team-user@example.com",
            null,
            CancellationToken.None
        );
        var user = await _context.Users.SingleAsync(user =>
            user.Email == "stale-team-user@example.com"
        );
        var activated = await AddActivatedOrganizationMembershipAsync(
            user.Id,
            "Stale Team Activated Org",
            isDefault: false,
            createdAtUtc: DateTimeOffset.UtcNow
        );
        var organizationMembership = await _context.OrganizationMemberships.SingleAsync(
            membership =>
                membership.UserId == user.Id
                && membership.OrganizationId == activated.OrganizationId
        );
        organizationMembership.DisabledAtUtc = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        Func<Task> act = async () =>
            await store.EnsureUserAsync(
                "mock",
                providerSubject,
                "Stale Team User",
                "stale-team-user@example.com",
                null,
                CancellationToken.None
            );

        await Assert.That(act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EnsureUserAsync_ExistingIdentity_PrefersActivatedOrganization()
    {
        var providerSubject = Guid.NewGuid().ToString("N");
        var store = new PostgresZeeqIdentityStore(_context);
        var initialContext = await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "activated-user@example.com",
            null,
            CancellationToken.None
        );

        var activated = await AddActivatedOrganizationMembershipAsync(
            initialContext.UserId,
            "Activated Org",
            isDefault: false,
            createdAtUtc: DateTimeOffset.UtcNow
        );

        var nextContext = await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "activated-user@example.com",
            null,
            CancellationToken.None
        );

        await Assert.That(initialContext.OrganizationId).IsNotEqualTo(activated.OrganizationId);
        await Assert.That(nextContext.OrganizationId).IsEqualTo(activated.OrganizationId);
        await Assert.That(nextContext.TeamId).IsEqualTo(activated.TeamId);
    }

    [Test]
    public async Task EnsureUserAsync_ExistingIdentity_PrefersDefaultActivatedOrganization()
    {
        var providerSubject = Guid.NewGuid().ToString("N");
        var store = new PostgresZeeqIdentityStore(_context);
        var initialContext = await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "default-activated-user@example.com",
            null,
            CancellationToken.None
        );

        await AddActivatedOrganizationMembershipAsync(
            initialContext.UserId,
            "Earlier Activated Org",
            isDefault: false,
            createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-2)
        );
        var defaultActivated = await AddActivatedOrganizationMembershipAsync(
            initialContext.UserId,
            "Default Activated Org",
            isDefault: true,
            createdAtUtc: DateTimeOffset.UtcNow
        );

        var nextContext = await store.EnsureUserAsync(
            "mock",
            providerSubject,
            "Domain User",
            "default-activated-user@example.com",
            null,
            CancellationToken.None
        );

        await Assert.That(nextContext.OrganizationId).IsEqualTo(defaultActivated.OrganizationId);
        await Assert.That(nextContext.TeamId).IsEqualTo(defaultActivated.TeamId);
    }

    private async Task<(
        string OrganizationId,
        string TeamId
    )> AddActivatedOrganizationMembershipAsync(
        string userId,
        string displayName,
        bool isDefault,
        DateTimeOffset createdAtUtc
    )
    {
        var organizationId = "org_" + Guid.NewGuid().ToString("N");
        var teamId = "team_" + Guid.NewGuid().ToString("N");
        _context.Organizations.Add(
            new Organization
            {
                Id = organizationId,
                DisplayName = displayName,
                Slug = OrganizationSlugGenerator.Create(displayName, organizationId),
                CreatedByUserId = userId,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc,
                ActivatedAtUtc = createdAtUtc,
            }
        );
        _context.Teams.Add(
            new Team
            {
                Id = teamId,
                OrganizationId = organizationId,
                DisplayName = displayName + " Root Team",
                IsRootTeam = true,
                CreatedByUserId = userId,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc,
            }
        );
        _context.OrganizationMemberships.Add(
            new OrganizationMembership
            {
                Id = "mem_" + Guid.NewGuid().ToString("N"),
                OrganizationId = organizationId,
                UserId = userId,
                Role = "member",
                Status = MembershipStatus.Active,
                IsDefault = isDefault,
                CreatedByUserId = userId,
                CreatedAtUtc = createdAtUtc,
            }
        );
        _context.TeamMemberships.Add(
            new TeamMembership
            {
                OrganizationId = organizationId,
                TeamId = teamId,
                UserId = userId,
                Role = "member",
                CreatedByUserId = userId,
                CreatedAtUtc = createdAtUtc,
            }
        );
        await _context.SaveChangesAsync();

        return (organizationId, teamId);
    }

    private async Task<(string OrganizationId, string OwnerId)> SeedSameDomainOrganizationAsync(
        string domain,
        string defaultRole
    )
    {
        var ownerId = "owner_" + Guid.NewGuid().ToString("N");
        var organizationId = "org_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        _context.Users.Add(
            new User
            {
                Id = ownerId,
                DisplayName = "Domain Owner",
                Email = "owner@" + domain,
                EmailVerified = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }
        );
        _context.Organizations.Add(
            new Organization
            {
                Id = organizationId,
                DisplayName = "Domain Org",
                CreatedByUserId = ownerId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                AutoInviteSameDomainEnabled = true,
                AutoInviteSameDomain = domain,
                AutoInviteDefaultRole = defaultRole,
            }
        );

        await _context.SaveChangesAsync();

        return (organizationId, ownerId);
    }

    [Test]
    public async Task RevokeUserTokensForOrganizationMemberAsync_RevokesOnlyMatchingActiveTokens()
    {
        var store = new PostgresZeeqIdentityStore(_context);
        var (orgId, teamId, userId) = await SeedOrgUserTeamAsync();
        var (otherOrgId, otherTeamId, _) = await SeedOrgUserTeamAsync();
        var (_, _, otherUserId) = await SeedOrgUserTeamAsync();

        var targetToken = NewUserToken(orgId, teamId, userId);
        var alreadyRevokedToken = NewUserToken(orgId, teamId, userId);
        alreadyRevokedToken.RevokedAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
        var otherOrgToken = NewUserToken(otherOrgId, otherTeamId, userId);
        var otherUserToken = NewUserToken(orgId, teamId, otherUserId);

        _context.UserTokens.AddRange(
            targetToken,
            alreadyRevokedToken,
            otherOrgToken,
            otherUserToken
        );
        await _context.SaveChangesAsync();

        var revokedAt = DateTimeOffset.UtcNow;
        var revokedCount = await store.RevokeUserTokensForOrganizationMemberAsync(
            orgId,
            userId,
            revokedAt,
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();

        // Guards that only the un-revoked token matching (orgId, userId) is
        // touched — the already-revoked, other-org, and other-user tokens
        // must be left untouched.
        await Assert.That(revokedCount).IsEqualTo(1);
        var reloadedTarget = await _context.UserTokens.SingleAsync(t => t.Id == targetToken.Id);
        await Assert.That(reloadedTarget.RevokedAtUtc).IsEqualTo(revokedAt);
        var reloadedOtherOrg = await _context.UserTokens.SingleAsync(t => t.Id == otherOrgToken.Id);
        await Assert.That(reloadedOtherOrg.RevokedAtUtc).IsNull();
        var reloadedOtherUser = await _context.UserTokens.SingleAsync(t =>
            t.Id == otherUserToken.Id
        );
        await Assert.That(reloadedOtherUser.RevokedAtUtc).IsNull();
    }

    [Test]
    public async Task RevokeUserTokensForOrganizationMemberAsync_IsIdempotentAgainstAlreadyRevokedRows()
    {
        var store = new PostgresZeeqIdentityStore(_context);
        var (orgId, teamId, userId) = await SeedOrgUserTeamAsync();
        var firstRevokedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var token = NewUserToken(orgId, teamId, userId);
        token.RevokedAtUtc = firstRevokedAt;

        _context.UserTokens.Add(token);
        await _context.SaveChangesAsync();

        var revokedCount = await store.RevokeUserTokensForOrganizationMemberAsync(
            orgId,
            userId,
            DateTimeOffset.UtcNow,
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();

        // Guards that re-running the revoke does not touch rows already
        // revoked (e.g. by a prior, since-lost call) and stamp a new time.
        await Assert.That(revokedCount).IsEqualTo(0);
        var reloaded = await _context.UserTokens.SingleAsync(t => t.Id == token.Id);
        await Assert.That(reloaded.RevokedAtUtc).IsEqualTo(firstRevokedAt);
    }

    private static UserToken NewUserToken(
        string organizationId,
        string teamId,
        string ownerUserId
    ) =>
        new()
        {
            Id = "auth_tok_" + Guid.NewGuid().ToString("N"),
            OwnerUserId = ownerUserId,
            OrganizationId = organizationId,
            TeamId = teamId,
            OwnerProvider = "mock",
            OwnerProviderSubject = Guid.NewGuid().ToString("N"),
            DisplayName = "Test Token",
            SelectedPartitionIdsJson = "[]",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30),
        };

    /// <summary>
    /// Seeds a minimal Organization/Team/User row set that satisfies the
    /// foreign keys on <c>auth_user_tokens</c>, returning the new IDs.
    /// </summary>
    private async Task<(string OrganizationId, string TeamId, string UserId)> SeedOrgUserTeamAsync()
    {
        var organizationId = "org_" + Guid.NewGuid().ToString("N");
        var teamId = "team_" + Guid.NewGuid().ToString("N");
        var userId = "user_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        _context.Users.Add(
            new User
            {
                Id = userId,
                DisplayName = "Token Test User",
                Email = $"{userId}@example.com",
                EmailVerified = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }
        );
        _context.Organizations.Add(
            new Organization
            {
                Id = organizationId,
                DisplayName = "Token Test Org",
                CreatedByUserId = userId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ActivatedAtUtc = now,
            }
        );
        _context.Teams.Add(
            new Team
            {
                Id = teamId,
                OrganizationId = organizationId,
                DisplayName = "Token Test Root Team",
                IsRootTeam = true,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }
        );

        await _context.SaveChangesAsync();

        return (organizationId, teamId, userId);
    }
}
