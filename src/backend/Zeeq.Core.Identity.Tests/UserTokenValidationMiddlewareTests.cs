using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Handler unit tests for <see cref="UserTokenValidationMiddleware"/>,
/// covering the pre-existing token-row checks and the new membership-
/// activation check (Change 2 of
/// <c>.agents/plans/2026-07-22-revoke-user-tokens-on-member-removal.spec.md</c>).
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/UserTokenValidationMiddlewareTests/*"
/// </summary>
public sealed class UserTokenValidationMiddlewareTests
{
    private const string TokenId = "auth_tok_test";
    private const string OrgId = "org_test";
    private const string UserId = "user_test";

    [Test]
    public async Task ActiveMembership_CallsNextAndMarksTokenUsed()
    {
        var identityStore = new FakeIdentityStore(NewToken(), markUsedResult: true);
        var membershipStore = new FakeMembershipStore(
            new MembershipActivationState(OrgId, UserId, MembershipStatus.Active, false)
        );

        var (statusCode, nextCalled) = await InvokeAsync(identityStore, membershipStore);

        await Assert.That(nextCalled).IsTrue();
        await Assert.That(statusCode).IsEqualTo(StatusCodes.Status200OK);
        await Assert.That(identityStore.MarkUsedCalls).IsEqualTo(1);
    }

    [Test]
    public async Task MissingMembership_Returns403AndDoesNotCallNext()
    {
        var identityStore = new FakeIdentityStore(NewToken(), markUsedResult: true);
        var membershipStore = new FakeMembershipStore(state: null);

        var (statusCode, nextCalled) = await InvokeAsync(identityStore, membershipStore);

        await Assert.That(nextCalled).IsFalse();
        await Assert.That(statusCode).IsEqualTo(StatusCodes.Status403Forbidden);
        await Assert.That(identityStore.MarkUsedCalls).IsEqualTo(0);
    }

    [Test]
    public async Task DisabledMembership_Returns403AndDoesNotCallNext()
    {
        var identityStore = new FakeIdentityStore(NewToken(), markUsedResult: true);
        var membershipStore = new FakeMembershipStore(
            new MembershipActivationState(OrgId, UserId, MembershipStatus.Disabled, true)
        );

        var (statusCode, nextCalled) = await InvokeAsync(identityStore, membershipStore);

        await Assert.That(nextCalled).IsFalse();
        await Assert.That(statusCode).IsEqualTo(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task RevokedToken_Returns401AndDoesNotCheckMembership()
    {
        var token = NewToken();
        token.RevokedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var identityStore = new FakeIdentityStore(token, markUsedResult: true);
        var membershipStore = new FakeMembershipStore(
            new MembershipActivationState(OrgId, UserId, MembershipStatus.Active, false)
        );

        var (statusCode, nextCalled) = await InvokeAsync(identityStore, membershipStore);

        // Guards that the existing 401 revocation path still wins and never
        // reaches the new membership-activation check.
        await Assert.That(nextCalled).IsFalse();
        await Assert.That(statusCode).IsEqualTo(StatusCodes.Status401Unauthorized);
        await Assert.That(membershipStore.LookupCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ExpiredToken_Returns401AndDoesNotCheckMembership()
    {
        var token = NewToken(expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        var identityStore = new FakeIdentityStore(token, markUsedResult: true);
        var membershipStore = new FakeMembershipStore(
            new MembershipActivationState(OrgId, UserId, MembershipStatus.Active, false)
        );

        var (statusCode, nextCalled) = await InvokeAsync(identityStore, membershipStore);

        await Assert.That(nextCalled).IsFalse();
        await Assert.That(statusCode).IsEqualTo(StatusCodes.Status401Unauthorized);
        await Assert.That(membershipStore.LookupCalls).IsEqualTo(0);
    }

    private static UserToken NewToken(DateTimeOffset? expiresAtUtc = null) =>
        new()
        {
            Id = TokenId,
            OwnerUserId = UserId,
            OrganizationId = OrgId,
            TeamId = "team_test",
            OwnerProvider = "mock",
            OwnerProviderSubject = "subject",
            DisplayName = "Test Token",
            SelectedPartitionIdsJson = "[]",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAtUtc = expiresAtUtc ?? DateTimeOffset.UtcNow.AddDays(30),
        };

    private static async Task<(int StatusCode, bool NextCalled)> InvokeAsync(
        FakeIdentityStore identityStore,
        FakeMembershipStore membershipStore
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IZeeqIdentityStore>(identityStore);
        services.AddSingleton<IZeeqMembershipStore>(membershipStore);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        await using var serviceProvider = services.BuildServiceProvider();

        var nextCalled = false;
        var appBuilder = new ApplicationBuilder(serviceProvider);
        appBuilder.UseUserTokenValidation();
        appBuilder.Run(context =>
        {
            nextCalled = true;
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var pipeline = appBuilder.Build();

        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(AuthClaims.UserTokenId, TokenId),
                    new System.Security.Claims.Claim(OpenIddictConstants.Claims.Subject, UserId),
                ],
                "test"
            )
        );

        await pipeline(httpContext);

        return (httpContext.Response.StatusCode, nextCalled);
    }

    private sealed class FakeIdentityStore(UserToken? token, bool markUsedResult)
        : IZeeqIdentityStore
    {
        public int MarkUsedCalls { get; private set; }

        public Task<UserToken?> FindUserTokenAsync(string tokenId, CancellationToken ct) =>
            Task.FromResult(token);

        public Task<bool> MarkUserTokenUsedAsync(
            string tokenId,
            string ownerUserId,
            DateTimeOffset usedAtUtc,
            CancellationToken ct
        )
        {
            MarkUsedCalls++;
            return Task.FromResult(markUsedResult);
        }

        public Task<AuthContext> EnsureUserAsync(
            string provider,
            string providerSubject,
            string? displayName,
            string? email,
            string? pictureUrl,
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

        public Task<bool> DeleteUserTokenAsync(
            string tokenId,
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<int> RevokeUserTokensForOrganizationMemberAsync(
            string organizationId,
            string ownerUserId,
            DateTimeOffset revokedAtUtc,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class FakeMembershipStore(MembershipActivationState? state)
        : IZeeqMembershipStore
    {
        public int LookupCalls { get; private set; }

        public Task<MembershipActivationState?> FindMembershipActivationStateAsync(
            string orgId,
            string userId,
            CancellationToken ct
        )
        {
            LookupCalls++;
            return Task.FromResult(state);
        }

        public Task<string?> FindOrganizationIdForMembershipAsync(
            string membershipId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
            string[] orgIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

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
        ) => throw new NotSupportedException();

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
        ) => throw new NotSupportedException();

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
    }
}
