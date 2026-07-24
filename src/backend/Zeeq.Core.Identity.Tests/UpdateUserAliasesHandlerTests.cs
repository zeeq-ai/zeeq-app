using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Handler tests for the org-scoped alias replacement endpoint.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Core.Identity.Tests --output detailed --disable-logo --treenode-filter "/*/*/UpdateUserAliasesHandlerTests/*"
/// </summary>
public sealed class UpdateUserAliasesHandlerTests
{
    [Test]
    public async Task HandleAsync_WithNullAliasArrays_ReturnsBadRequestWithoutReplacingAliases()
    {
        var identityStore = new TestIdentityStore();
        var handler = new UpdateUserAliasesHandler(identityStore);

        var result = await handler.HandleAsync(
            "org_123",
            new UpdateUserAliasesRequest(null!, null!),
            TestPrincipal("usr_123"),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<IdentityEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_alias");
        await Assert.That(identityStore.ReplaceCalled).IsFalse();
    }

    private static ClaimsPrincipal TestPrincipal(string userId) =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, userId)],
                authenticationType: "Test"
            )
        );

    private sealed class TestIdentityStore : IZeeqIdentityStore
    {
        public bool ReplaceCalled { get; private set; }

        public Task<AuthContext> EnsureUserAsync(
            string provider,
            string providerSubject,
            string? displayName,
            string? email,
            string? pictureUrl,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<string?> FindUserEmailAsync(string userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UserAlias>> ListUserAliasesAsync(
            string organizationId,
            string userId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<UserAlias>> ReplaceUserAliasesAsync(
            string organizationId,
            string userId,
            IReadOnlyList<UserAliasWrite> aliases,
            CancellationToken cancellationToken
        )
        {
            ReplaceCalled = true;
            return Task.FromResult<IReadOnlyList<UserAlias>>([]);
        }

        public Task CreatePendingDcrSetupAsync(
            DcrClientSetup setup,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<DcrClientSetup?> FindDcrSetupAsync(
            string clientId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task MarkDcrSetupExpiredAsync(string clientId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ClaimDcrSetupAsync(
            string clientId,
            OwnerContext owner,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<ClientCredential>> ListClientCredentialsAsync(
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ClientCredential?> FindClientCredentialAsync(
            string clientId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task AddClientCredentialAsync(
            ClientCredential credential,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteClientCredentialAsync(
            string clientId,
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task AddUserTokenAsync(
            UserToken token,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task RemoveUserTokenAsync(string tokenId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UserToken>> ListUserTokensAsync(
            string ownerUserId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<UserToken?> FindUserTokenAsync(
            string tokenId,
            CancellationToken cancellationToken
        ) =>
            throw new NotSupportedException();

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
