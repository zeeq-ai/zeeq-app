using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership.Tests;

/// <inheritdoc cref="IZeeqIdentityStore" />
/// <remarks>
/// Minimal in-memory store for handler branch tests.
/// <para>
/// Only <see cref="RevokeUserTokensForOrganizationMemberAsync"/> is exercised
/// by <see cref="MembershipEndpointHandlerTests"/> today; every other member
/// is unused by those tests and throws to flag an accidental new dependency.
/// </para>
/// </remarks>
internal sealed class TestIdentityStore : IZeeqIdentityStore
{
    public List<UserToken> Tokens { get; } = [];
    public int RevokeCalls { get; private set; }
    public bool ThrowOnRevoke { get; set; }

    /// <inheritdoc />
    public Task<int> RevokeUserTokensForOrganizationMemberAsync(
        string organizationId,
        string ownerUserId,
        DateTimeOffset revokedAtUtc,
        CancellationToken ct
    )
    {
        RevokeCalls++;

        if (ThrowOnRevoke)
        {
            throw new InvalidOperationException("Simulated revoke failure.");
        }

        var matching = Tokens
            .Where(token =>
                token.OrganizationId == organizationId
                && token.OwnerUserId == ownerUserId
                && token.RevokedAtUtc is null
            )
            .ToArray();

        foreach (var token in matching)
        {
            token.RevokedAtUtc = revokedAtUtc;
        }

        return Task.FromResult(matching.Length);
    }

    public Task<AuthContext> EnsureUserAsync(
        string provider,
        string providerSubject,
        string? displayName,
        string? email,
        string? pictureUrl,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task CreatePendingDcrSetupAsync(
        DcrClientSetup setup,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<DcrClientSetup?> FindDcrSetupAsync(
        string clientId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task MarkDcrSetupExpiredAsync(string clientId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task ClaimDcrSetupAsync(
        string clientId,
        OwnerContext owner,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<IReadOnlyList<ClientCredential>> ListClientCredentialsAsync(
        string ownerUserId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task AddClientCredentialAsync(
        ClientCredential credential,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<ClientCredential?> FindClientCredentialAsync(
        string clientId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<bool> DeleteClientCredentialAsync(
        string clientId,
        string ownerUserId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<IReadOnlyList<UserAlias>> ListUserAliasesAsync(
        string organizationId,
        string userId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<IReadOnlyList<UserAlias>> ReplaceUserAliasesAsync(
        string organizationId,
        string userId,
        IReadOnlyList<UserAliasWrite> aliases,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<IReadOnlyList<UserToken>> ListUserTokensAsync(
        string ownerUserId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task AddUserTokenAsync(UserToken token, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task RemoveUserTokenAsync(string tokenId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<UserToken?> FindUserTokenAsync(
        string tokenId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<bool> DeleteUserTokenAsync(
        string tokenId,
        string ownerUserId,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    public Task<bool> MarkUserTokenUsedAsync(
        string tokenId,
        string ownerUserId,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();
}
