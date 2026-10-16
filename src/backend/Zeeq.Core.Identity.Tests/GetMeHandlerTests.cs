using System.Collections;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Handler tests for the authenticated /me identity projection.
///
/// Run all identity tests:
/// dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/GetMeHandlerTests/*"
/// </summary>
public sealed class GetMeHandlerTests
{
    [Test]
    public async Task HandleAsync_WithOrganizationIcon_IncludesIconUrlInOrgSummary()
    {
        var org = new Organization
        {
            Id = "org_123",
            DisplayName = "My Cool Org",
            Slug = "my-cool-org",
            IconUrl = "data:image/png;base64,abc",
            CreatedByUserId = "usr_123",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var store = new TestMembershipStore([org], [ActiveMembership(org.Id, "usr_123")]);
        var handler = CreateHandler(store);

        // Guards that the /me org list includes icon metadata so shell
        // navigation refreshes after organization settings save a new icon.
        var result = await InvokeHandleAsync(
            handler,
            TestPrincipal("usr_123", org.Id),
            CancellationToken.None
        );
        var response = GetOkValue(result);
        var organizations = GetProperty<IEnumerable>(response, "Organizations")
            .Cast<object>()
            .ToArray();
        var orgSummary = organizations.Single();

        await Assert.That(GetProperty<string?>(response, "UserId")).IsEqualTo("usr_123");
        await Assert.That(organizations).HasSingleItem();
        await Assert.That(GetProperty<string?>(orgSummary, "IconUrl")).IsEqualTo(org.IconUrl);
    }

    [Test]
    public async Task HandleAsync_WithConfiguredSystemAdminSubject_ReturnsIsSystemAdminTrue()
    {
        var org = TestOrganization();
        var store = new TestMembershipStore([org], [ActiveMembership(org.Id, "usr_123")]);
        var handler = CreateHandler(store, ["google:sub_123"]);

        var result = await InvokeHandleAsync(
            handler,
            TestPrincipal("usr_123", org.Id, provider: "google", providerSubject: "sub_123"),
            CancellationToken.None
        );
        var response = GetOkValue(result);

        await Assert.That(GetProperty<bool>(response, "IsSystemAdmin")).IsTrue();
    }

    [Test]
    public async Task HandleAsync_WithStaleSystemAdminRoleButUnconfiguredSubject_ReturnsIsSystemAdminFalse()
    {
        var org = TestOrganization();
        var store = new TestMembershipStore([org], [ActiveMembership(org.Id, "usr_123")]);
        var handler = CreateHandler(store, ["google:other-subject"]);

        var result = await InvokeHandleAsync(
            handler,
            TestPrincipal(
                "usr_123",
                org.Id,
                provider: "google",
                providerSubject: "sub_123",
                roles: [SystemRoles.SystemAdmin]
            ),
            CancellationToken.None
        );
        var response = GetOkValue(result);

        await Assert.That(GetProperty<bool>(response, "IsSystemAdmin")).IsFalse();
    }

    [Test]
    public async Task HandleAsync_WithUserAliases_ReturnsActiveOrganizationAliases()
    {
        var org = TestOrganization();
        var membershipStore = new TestMembershipStore([org], [ActiveMembership(org.Id, "usr_123")]);
        var identityStore = new TestIdentityStore([
            new UserAlias
            {
                Id = "alias_123",
                OrganizationId = org.Id,
                UserId = "usr_123",
                Kind = UserAliasKind.Email,
                DisplayValue = "personal@example.com",
                NormalizedValue = "personal@example.com",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
        ]);
        var handler = CreateHandler(membershipStore, identityStore: identityStore);

        var result = await InvokeHandleAsync(
            handler,
            TestPrincipal("usr_123", org.Id),
            CancellationToken.None
        );
        var response = GetOkValue(result);
        var aliases = GetProperty<IEnumerable>(response, "Aliases").Cast<object>().ToArray();

        await Assert.That(aliases).HasSingleItem();
        await Assert
            .That(GetProperty<UserAliasKind>(aliases[0], "Kind"))
            .IsEqualTo(UserAliasKind.Email);
        await Assert
            .That(GetProperty<string>(aliases[0], "Value"))
            .IsEqualTo("personal@example.com");
    }

    [Test]
    public async Task HandleAsync_WithoutEmailClaim_ReturnsProfileAndSkipsInvitationLookup()
    {
        var org = TestOrganization();
        var store = new TestMembershipStore([org], [ActiveMembership(org.Id, "usr_123")]);
        var handler = CreateHandler(store);

        // Guards that external identities without an email claim (e.g. the MockOAuth
        // default user) still get a valid /me response instead of a NullReferenceException
        // from the invitation lookup, and that no email is fabricated.
        var result = await InvokeHandleAsync(
            handler,
            TestPrincipal("usr_123", org.Id, email: null),
            CancellationToken.None
        );
        var response = GetOkValue(result);
        var organizations = GetProperty<IEnumerable>(response, "Organizations")
            .Cast<object>()
            .ToArray();

        await Assert.That(GetProperty<string>(response, "UserId")).IsEqualTo("usr_123");
        await Assert.That(GetProperty<string>(response, "OrganizationId")).IsEqualTo(org.Id);
        await Assert.That(response.GetType().GetProperty("Email")?.GetValue(response)).IsNull();
        await Assert.That(organizations).HasSingleItem();
        await Assert
            .That(GetProperty<MembershipStatus>(organizations[0], "Status"))
            .IsEqualTo(MembershipStatus.Active);
        await Assert.That(store.PendingInvitationLookupEmails).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task HandleAsync_WithBlankEmailClaim_SkipsInvitationLookupAndReturnsNoPendingOrgs(
        string email
    )
    {
        var org = TestOrganization();
        var store = new TestMembershipStore([org], [ActiveMembership(org.Id, "usr_123")]);
        var handler = CreateHandler(store);

        // Guards that a blank email claim is treated like a missing one rather than
        // being passed to the invitation lookup.
        var result = await InvokeHandleAsync(
            handler,
            TestPrincipal("usr_123", org.Id, email: email),
            CancellationToken.None
        );
        var response = GetOkValue(result);
        var organizations = GetProperty<IEnumerable>(response, "Organizations")
            .Cast<object>()
            .ToArray();

        await Assert.That(organizations).HasSingleItem();
        await Assert
            .That(GetProperty<MembershipStatus>(organizations[0], "Status"))
            .IsEqualTo(MembershipStatus.Active);
        await Assert.That(store.PendingInvitationLookupEmails).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_WithEmailClaim_MergesPendingInvitationsIntoOrgList()
    {
        var activeOrg = TestOrganization();
        var invitingOrg = new Organization
        {
            Id = "org_456",
            DisplayName = "Inviting Org",
            Slug = "inviting-org",
            CreatedByUserId = "usr_999",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var invitation = new OrganizationMembership
        {
            Id = "mem_invite",
            OrganizationId = invitingOrg.Id,
            Role = "member",
            Status = MembershipStatus.Pending,
            InvitedEmail = "user@example.com",
            CreatedByUserId = "usr_999",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var store = new TestMembershipStore(
            [activeOrg, invitingOrg],
            [ActiveMembership(activeOrg.Id, "usr_123")],
            [invitation]
        );
        var handler = CreateHandler(store);

        // Guards that real-email callers keep seeing their pending invitations,
        // alongside active orgs, after the missing-email guard was added.
        var result = await InvokeHandleAsync(
            handler,
            TestPrincipal("usr_123", activeOrg.Id),
            CancellationToken.None
        );
        var response = GetOkValue(result);
        var organizations = GetProperty<IEnumerable>(response, "Organizations")
            .Cast<object>()
            .ToArray();

        await Assert.That(store.PendingInvitationLookupEmails).HasSingleItem();
        await Assert.That(organizations.Length).IsEqualTo(2);
        await Assert
            .That(GetProperty<MembershipStatus>(organizations[0], "Status"))
            .IsEqualTo(MembershipStatus.Active);
        await Assert
            .That(GetProperty<MembershipStatus>(organizations[1], "Status"))
            .IsEqualTo(MembershipStatus.Pending);
        await Assert
            .That(GetProperty<string>(organizations[1], "InvitationId"))
            .IsEqualTo("mem_invite");
    }

    [Test]
    public async Task UserAliasEndpointMapping_SerializesKindWithCustomWireName()
    {
        var alias = new UserAlias
        {
            Id = "alias_123",
            OrganizationId = "org_123",
            UserId = "usr_123",
            Kind = UserAliasKind.GitHub,
            DisplayValue = "alias",
            NormalizedValue = "alias",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(
            UserAliasEndpointMapping.ToDto(alias),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        );

        await Assert.That(json).Contains("\"kind\":\"github\"");
    }

    private static async Task<object> InvokeHandleAsync(
        GetMeHandler handler,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var method =
            typeof(GetMeHandler).GetMethod(
                "HandleAsync",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new InvalidOperationException("Could not find GetMeHandler.HandleAsync.");
        var task =
            method.Invoke(handler, [user, ct]) as Task
            ?? throw new InvalidOperationException(
                "GetMeHandler.HandleAsync did not return a Task."
            );

        await task;

        return task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException("GetMeHandler.HandleAsync returned no result.");
    }

    private static object GetOkValue(object result)
    {
        var innerResult =
            result.GetType().GetProperty("Result")?.GetValue(result)
            ?? throw new InvalidOperationException("Expected typed result union.");

        return innerResult.GetType().GetProperty("Value")?.GetValue(innerResult)
            ?? throw new InvalidOperationException("Expected Ok result with a value.");
    }

    private static T GetProperty<T>(object value, string propertyName) =>
        (T)(
            value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new InvalidOperationException($"Expected {propertyName} property.")
        );

    private static GetMeHandler CreateHandler(
        IZeeqMembershipStore store,
        string[]? systemAdminSubjects = null,
        IZeeqIdentityStore? identityStore = null
    ) =>
        new(
            store,
            identityStore ?? new TestIdentityStore([]),
            new SystemAdminEvaluator(
                new AppSettings
                {
                    Platform = new PlatformSettings
                    {
                        SystemAdminSubjects = systemAdminSubjects ?? [],
                    },
                }
            )
        );

    private static ClaimsPrincipal TestPrincipal(
        string userId,
        string orgId,
        string provider = "mock",
        string providerSubject = "mock_123",
        string[]? roles = null,
        string? email = "user@example.com"
    ) =>
        new(
            new ClaimsIdentity(
                BuildClaims(
                    [
                        new Claim(OpenIddictConstants.Claims.Subject, userId),
                        new Claim(AuthClaims.OrganizationId, orgId),
                        .. email is null
                            ? Array.Empty<Claim>()
                            : [new Claim(OpenIddictConstants.Claims.Email, email)],
                        new Claim(AuthClaims.Provider, provider),
                        new Claim(AuthClaims.ProviderSubject, providerSubject),
                    ],
                    roles
                ),
                authenticationType: "Test"
            )
        );

    private static IEnumerable<Claim> BuildClaims(Claim[] claims, string[]? roles)
    {
        foreach (var claim in claims)
        {
            yield return claim;
        }

        foreach (var role in roles ?? [])
        {
            yield return new Claim(OpenIddictConstants.Claims.Role, role);
        }
    }

    private static Organization TestOrganization() =>
        new()
        {
            Id = "org_123",
            DisplayName = "My Cool Org",
            Slug = "my-cool-org",
            IconUrl = "data:image/png;base64,abc",
            CreatedByUserId = "usr_123",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static OrganizationMembership ActiveMembership(string organizationId, string userId) =>
        new()
        {
            Id = "mem_123",
            OrganizationId = organizationId,
            UserId = userId,
            Role = "owner",
            Status = MembershipStatus.Active,
            IsDefault = true,
            CreatedByUserId = userId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class TestMembershipStore(
        IReadOnlyList<Organization> organizations,
        IReadOnlyList<OrganizationMembership> memberships,
        IReadOnlyList<OrganizationMembership>? pendingInvitations = null
    ) : IZeeqMembershipStore
    {
        public List<string> PendingInvitationLookupEmails { get; } = [];

        public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
            Task.FromResult(organizations.FirstOrDefault(org => org.Id == orgId));

        public Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
            string[] orgIds,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<Organization>>(
                organizations.Where(org => orgIds.Contains(org.Id)).ToArray()
            );

        public Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
            string orgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> IsSlugAvailableAsync(
            string slug,
            string? excludeOrgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task UpdateOrganizationAsync(Organization org, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> UpdateOrganizationSameDomainOnboardingAsync(
            Organization organization,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<string?> FindUserEmailByIdAsync(string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> FindUserEmailsByIdsAsync(
            string[] userIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> IsAutoInviteSameDomainAvailableAsync(
            string domain,
            string excludeOrgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> FindAutoInviteSameDomainClaimsAsync(
            string[] domains,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> CountOrganizationsCreatedByUserAsync(
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Organization?> CreateOrganizationAsync(
            Organization organization,
            Team rootTeam,
            OrganizationMembership ownerMembership,
            TeamMembership rootTeamMembership,
            int maxCreatedOrganizations,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationMembership>> ListActiveMembershipsForUserAsync(
            string userId,
            CancellationToken ct
        ) =>
            Task.FromResult<IReadOnlyList<OrganizationMembership>>(
                memberships
                    .Where(membership =>
                        membership.UserId == userId && membership.Status == MembershipStatus.Active
                    )
                    .ToArray()
            );

        public Task<IReadOnlyList<OrganizationMember>> ListMembersForOrganizationAsync(
            string orgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetDefaultOrganizationAsync(
            string userId,
            string orgId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task UpdateMemberRoleAsync(
            string orgId,
            string userId,
            string newRole,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string?> FindRootTeamIdForMemberAsync(
            string orgId,
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<OrganizationMembership> CreateInvitationAsync(
            OrganizationMembership invitation,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForEmailAsync(
            string email,
            CancellationToken ct
        )
        {
            // Mirrors PostgresZeeqMembershipStore, which trims the email and throws on null.
            PendingInvitationLookupEmails.Add(email.Trim());

            return Task.FromResult<IReadOnlyList<OrganizationMembership>>(
                (pendingInvitations ?? [])
                    .Where(invitation =>
                        string.Equals(
                            invitation.InvitedEmail,
                            email.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToArray()
            );
        }

        public Task<
            IReadOnlyList<OrganizationMembership>
        > ListPendingInvitationsForOrganizationAsync(string orgId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> AcceptInvitationAsync(
            string membershipId,
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> AcceptInvitationAsDefaultAsync(
            string membershipId,
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<SameDomainInvitationDetails?> FindSameDomainInvitationDetailsAsync(
            string membershipId,
            string email,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<bool> DeclineInvitationAsync(string membershipId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> CancelInvitationAsync(
            string orgId,
            string membershipId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<MembershipActivationState?> FindMembershipActivationStateAsync(
            string orgId,
            string userId,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class TestIdentityStore(IReadOnlyList<UserAlias> aliases) : IZeeqIdentityStore
    {
        public Task<AuthContext?> EnsureUserAsync(
            string provider,
            string providerSubject,
            string? displayName,
            string? email,
            string? pictureUrl,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<string?> FindUserEmailAsync(
            string userId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<UserAlias>> ListUserAliasesAsync(
            string organizationId,
            string userId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<UserAlias>>(
                aliases
                    .Where(alias =>
                        alias.OrganizationId == organizationId && alias.UserId == userId
                    )
                    .ToArray()
            );

        public Task<IReadOnlyList<UserAlias>> ReplaceUserAliasesAsync(
            string organizationId,
            string userId,
            IReadOnlyList<UserAliasWrite> aliases,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task CreatePendingDcrSetupAsync(
            DcrClientSetup setup,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<DcrClientSetup?> FindDcrSetupAsync(
            string clientId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task MarkDcrSetupExpiredAsync(
            string clientId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task ClaimDcrSetupAsync(
            string clientId,
            OwnerContext owner,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<ClientCredential>> ListClientCredentialsAsync(
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task AddClientCredentialAsync(
            ClientCredential credential,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ClientCredential?> FindClientCredentialAsync(
            string clientId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteClientCredentialAsync(
            string clientId,
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<UserToken>> ListUserTokensAsync(
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task AddUserTokenAsync(UserToken token, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RemoveUserTokenAsync(string tokenId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserToken?> FindUserTokenAsync(
            string tokenId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteUserTokenAsync(
            string tokenId,
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> MarkUserTokenUsedAsync(
            string tokenId,
            string ownerUserId,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<int> RevokeUserTokensForOrganizationMemberAsync(
            string organizationId,
            string ownerUserId,
            DateTimeOffset revokedAtUtc,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
