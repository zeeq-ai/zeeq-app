using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Identity;

/// <inheritdoc cref="IZeeqIdentityStore" />
public sealed class PostgresZeeqIdentityStore(PostgresDbContext db) : IZeeqIdentityStore
{
    /// <inheritdoc />
    public async Task<AuthContext> EnsureUserAsync(
        string provider,
        string providerSubject,
        string? displayName,
        string? email,
        string? pictureUrl,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedName = string.IsNullOrWhiteSpace(displayName) ? "User" : displayName.Trim();
        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

        var existingIdentity = await db
            .ExternalUserIdentities.TagWithOperationCallSite(
                "identity.external_identity.ensure_user_find"
            )
            .SingleOrDefaultAsync(
                identity =>
                    identity.Provider == provider && identity.ProviderSubject == providerSubject,
                cancellationToken
            );

        if (existingIdentity is not null)
        {
            if (existingIdentity.DisabledAtUtc is not null)
            {
                throw new InvalidOperationException("The linked external identity is disabled.");
            }

            var user = await db
                .Users.TagWithOperationCallSite("identity.user.ensure_user_load_existing")
                .SingleAsync(item => item.Id == existingIdentity.UserId, cancellationToken);

            if (user.DisabledAtUtc is not null)
            {
                throw new InvalidOperationException("The local Zeeq user is disabled.");
            }

            existingIdentity.DisplayName = normalizedName;
            existingIdentity.Email = normalizedEmail;
            existingIdentity.PictureUrl = pictureUrl;
            existingIdentity.LastSeenAtUtc = now;

            user.DisplayName = normalizedName;
            user.Email = normalizedEmail;
            user.PictureUrl = pictureUrl;
            user.UpdatedAtUtc = now;
            user.LastLoginAtUtc = now;

            var context = await FindActiveContextAsync(user.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return context;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var userId = "usr_" + suffix;
        var organizationId = "org_" + suffix;
        var teamId = "team_" + suffix;
        var organizationSlug = OrganizationSlugGenerator.Create(normalizedName, organizationId);

        db.Users.Add(
            new User
            {
                Id = userId,
                DisplayName = normalizedName,
                Email = normalizedEmail,
                PictureUrl = pictureUrl,
                EmailVerified = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastLoginAtUtc = now,
            }
        );
        db.ExternalUserIdentities.Add(
            new ExternalUserIdentity
            {
                UserId = userId,
                Provider = provider,
                ProviderSubject = providerSubject,
                DisplayName = normalizedName,
                Email = normalizedEmail,
                PictureUrl = pictureUrl,
                EmailVerified = false,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
            }
        );
        db.Organizations.Add(
            new Organization
            {
                Id = organizationId,
                DisplayName = normalizedName + "'s Organization",
                Slug = organizationSlug,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }
        );
        db.Teams.Add(
            new Team
            {
                Id = teamId,
                OrganizationId = organizationId,
                DisplayName = normalizedName + "'s Team",
                IsRootTeam = true,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }
        );
        db.OrganizationMemberships.Add(
            new OrganizationMembership
            {
                Id = "mem_" + suffix,
                OrganizationId = organizationId,
                UserId = userId,
                Role = "owner",
                Status = MembershipStatus.Active,
                CreatedByUserId = userId,
                IsDefault = true,
                CreatedAtUtc = now,
            }
        );
        db.TeamMemberships.Add(
            new TeamMembership
            {
                OrganizationId = organizationId,
                TeamId = teamId,
                UserId = userId,
                Role = "owner",
                CreatedByUserId = userId,
                CreatedAtUtc = now,
            }
        );

        await db.SaveChangesAsync(cancellationToken);

        return new AuthContext(userId, organizationId, teamId);
    }

    /// <inheritdoc />
    public async Task CreatePendingDcrSetupAsync(
        DcrClientSetup setup,
        CancellationToken cancellationToken
    )
    {
        db.DcrClientSetups.Add(setup);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<DcrClientSetup?> FindDcrSetupAsync(
        string clientId,
        CancellationToken cancellationToken
    ) =>
        db
            .DcrClientSetups.TagWithOperationCallSite("identity.dcr_setup.find")
            .SingleOrDefaultAsync(setup => setup.ClientId == clientId, cancellationToken);

    /// <inheritdoc />
    public async Task MarkDcrSetupExpiredAsync(string clientId, CancellationToken cancellationToken)
    {
        await db
            .DcrClientSetups.TagWithOperationCallSite("identity.dcr_setup.mark_expired")
            .Where(setup =>
                setup.ClientId == clientId && setup.Status == DcrClientSetup.PendingLogin
            )
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(setup => setup.Status, DcrClientSetup.Expired),
                cancellationToken
            );
    }

    /// <inheritdoc />
    public async Task ClaimDcrSetupAsync(
        string clientId,
        OwnerContext owner,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        await db
            .DcrClientSetups.TagWithOperationCallSite("identity.dcr_setup.claim")
            .Where(setup =>
                setup.ClientId == clientId && setup.Status == DcrClientSetup.PendingLogin
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(setup => setup.Status, DcrClientSetup.Active)
                        .SetProperty(setup => setup.ClaimedAtUtc, now)
                        .SetProperty(setup => setup.ClaimedUserId, owner.UserId)
                        .SetProperty(setup => setup.OrganizationId, owner.OrganizationId)
                        .SetProperty(setup => setup.TeamId, owner.TeamId)
                        .SetProperty(
                            setup => setup.SelectedPartitionIdsJson,
                            owner.PartitionIdsJson
                        )
                        .SetProperty(setup => setup.ClaimedOwnerProvider, owner.Provider)
                        .SetProperty(
                            setup => setup.ClaimedOwnerProviderSubject,
                            owner.ProviderSubject
                        ),
                cancellationToken
            );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientCredential>> ListClientCredentialsAsync(
        string ownerUserId,
        CancellationToken cancellationToken
    ) =>
        await db
            .ClientCredentials.TagWithOperationCallSite("identity.client_credential.list")
            .AsNoTracking()
            .Where(credential => credential.OwnerUserId == ownerUserId)
            .OrderByDescending(credential => credential.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc />
    public async Task AddClientCredentialAsync(
        ClientCredential credential,
        CancellationToken cancellationToken
    )
    {
        db.ClientCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ClientCredential?> FindClientCredentialAsync(
        string clientId,
        CancellationToken cancellationToken
    ) =>
        db
            .ClientCredentials.TagWithOperationCallSite("identity.client_credential.find")
            .AsNoTracking()
            .SingleOrDefaultAsync(credential => credential.ClientId == clientId, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteClientCredentialAsync(
        string clientId,
        string ownerUserId,
        CancellationToken cancellationToken
    )
    {
        var deleted = await db
            .ClientCredentials.TagWithOperationCallSite("identity.client_credential.delete")
            .Where(item => item.ClientId == clientId && item.OwnerUserId == ownerUserId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserToken>> ListUserTokensAsync(
        string ownerUserId,
        CancellationToken cancellationToken
    ) =>
        await db
            .UserTokens.TagWithOperationCallSite("identity.user_token.list")
            .AsNoTracking()
            .Where(token => token.OwnerUserId == ownerUserId)
            .OrderByDescending(token => token.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc />
    public async Task AddUserTokenAsync(UserToken token, CancellationToken cancellationToken)
    {
        db.UserTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveUserTokenAsync(string tokenId, CancellationToken cancellationToken)
    {
        await db
            .UserTokens.TagWithOperationCallSite("identity.user_token.remove")
            .Where(token => token.Id == tokenId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<UserToken?> FindUserTokenAsync(
        string tokenId,
        CancellationToken cancellationToken
    ) =>
        db
            .UserTokens.TagWithOperationCallSite("identity.user_token.find")
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.Id == tokenId, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> DeleteUserTokenAsync(
        string tokenId,
        string ownerUserId,
        CancellationToken cancellationToken
    )
    {
        var deleted = await db
            .UserTokens.TagWithOperationCallSite("identity.user_token.delete")
            .Where(token => token.Id == tokenId && token.OwnerUserId == ownerUserId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<bool> MarkUserTokenUsedAsync(
        string tokenId,
        string ownerUserId,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken
    )
    {
        var updated = await db
            .UserTokens.TagWithOperationCallSite("identity.user_token.mark_used")
            .Where(token => token.Id == tokenId && token.OwnerUserId == ownerUserId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.LastUsedAtUtc, usedAtUtc),
                cancellationToken
            );

        return updated == 1;
    }

    private async Task<AuthContext> FindActiveContextAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        var teamMembership = await db
            .TeamMemberships.TagWithOperationCallSite("identity.auth_context.find_active")
            .AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.DisabledAtUtc == null)
            .OrderBy(membership => membership.CreatedAtUtc)
            .FirstAsync(cancellationToken);

        return new AuthContext(userId, teamMembership.OrganizationId, teamMembership.TeamId);
    }
}
