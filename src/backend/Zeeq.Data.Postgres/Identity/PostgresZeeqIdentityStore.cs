using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Identity;

/// <inheritdoc cref="IZeeqIdentityStore" />
public sealed class PostgresZeeqIdentityStore(PostgresDbContext db) : IZeeqIdentityStore
{
    private const string SameDomainAutoInviteIndexName =
        "ix_core_organization_memberships_same_domain_auto_invite";
    private const string UserAliasUniqueIndexName =
        "ix_core_user_aliases_organization_id_kind_normalized_value";
    private const int UserAliasReplacementLockNamespace = 722452;

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
                ActivatedAtUtc = now,
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

        var sameDomainInvitation = await CreateSameDomainInvitationIfEligibleAsync(
            organizationId,
            userId,
            normalizedEmail,
            now,
            cancellationToken
        );

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (sameDomainInvitation is not null
                && IsSameDomainAutoInviteUniqueViolation(exception)
            )
        {
            db.Entry(sameDomainInvitation).State = EntityState.Detached;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new AuthContext(userId, organizationId, teamId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserAlias>> ListUserAliasesAsync(
        string organizationId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        return await db
            .UserAliases.TagWithOperationCallSite("identity.user_aliases.list")
            .AsNoTracking()
            .Where(alias => alias.OrganizationId == organizationId && alias.UserId == userId)
            .Where(alias => alias.DisabledAtUtc == null)
            .OrderBy(alias => alias.Kind)
            .ThenBy(alias => alias.NormalizedValue)
            .ToArrayAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserAlias>> ReplaceUserAliasesAsync(
        string organizationId,
        string userId,
        IReadOnlyList<UserAliasWrite> aliases,
        CancellationToken cancellationToken
    )
    {
        var aliasWrites = aliases.Select(ValidateAliasWrite).ToArray();

        await using var transaction = await BeginTransactionIfNeededAsync(cancellationToken);

        // NOTE: PUT alias replacement is a read-modify-write operation over the user's full
        // active alias set. Serialize by (organization, user) so concurrent requests cannot
        // each add different aliases and leave the union active.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({UserAliasReplacementLockNamespace}, hashtext({organizationId + ":" + userId}))",
            cancellationToken
        );

        var isActiveMember = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "identity.user_aliases.replace_active_membership_check"
            )
            .AsNoTracking()
            .AnyAsync(
                membership =>
                    membership.OrganizationId == organizationId
                    && membership.UserId == userId
                    && membership.Status == MembershipStatus.Active
                    && membership.DisabledAtUtc == null,
                cancellationToken
            );

        if (!isActiveMember)
        {
            throw new InvalidOperationException(
                "The user is not an active member of this organization."
            );
        }

        var existing = await db
            .UserAliases.TagWithOperationCallSite("identity.user_aliases.replace_load_existing")
            .Where(alias => alias.OrganizationId == organizationId && alias.UserId == userId)
            .ToArrayAsync(cancellationToken);
        var requestedKeys = aliasWrites
            .Select(alias => new AliasKey(alias.Kind, alias.NormalizedValue))
            .ToHashSet();
        var requestedEmailAliases = aliasWrites
            .Where(alias => alias.Kind == UserAliasKind.Email)
            .Select(alias => alias.NormalizedValue)
            .ToArray();
        var requestedGitHubAliases = aliasWrites
            .Where(alias => alias.Kind == UserAliasKind.GitHub)
            .Select(alias => alias.NormalizedValue)
            .ToArray();

        var conflicting = await db
            .UserAliases.TagWithOperationCallSite("identity.user_aliases.replace_conflict_check")
            .AsNoTracking()
            .Where(alias =>
                alias.OrganizationId == organizationId
                && alias.UserId != userId
                && alias.DisabledAtUtc == null
                && (
                    (
                        alias.Kind == UserAliasKind.Email
                        && requestedEmailAliases.Contains(alias.NormalizedValue)
                    )
                    || (
                        alias.Kind == UserAliasKind.GitHub
                        && requestedGitHubAliases.Contains(alias.NormalizedValue)
                    )
                )
            )
            .Select(alias => alias.DisplayValue)
            .FirstOrDefaultAsync(cancellationToken);

        if (conflicting is not null)
        {
            throw new InvalidOperationException(
                $"Alias '{conflicting}' already belongs to another member in this organization."
            );
        }

        // NOTE: Email has a canonical member identity in core_users.email, so aliases must
        // not overlap another active member's email. GitHub currently has no separate
        // canonical member-login column; the org-scoped active alias unique index is the
        // GitHub identity boundary until that model exists.
        var conflictingCanonicalEmail = await (
            from membership in db.OrganizationMemberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId
            where membership.Status == MembershipStatus.Active
            where membership.DisabledAtUtc == null
            where user.DisabledAtUtc == null
            where user.Id != userId
            where user.Email != null
            where requestedEmailAliases.Contains(user.Email!.Trim().ToLower())
            select user.Email
        )
            .TagWithOperationCallSite("identity.user_aliases.replace_canonical_email_check")
            .FirstOrDefaultAsync(cancellationToken);

        if (conflictingCanonicalEmail is not null)
        {
            throw new InvalidOperationException(
                $"Alias '{conflictingCanonicalEmail}' already belongs to another member in this organization."
            );
        }

        var now = DateTimeOffset.UtcNow;
        var existingByKey = existing
            .GroupBy(alias => new AliasKey(alias.Kind, alias.NormalizedValue))
            .ToDictionary(
                group => group.Key,
                group =>
                    group
                        .OrderBy(alias => alias.DisabledAtUtc is not null)
                        .ThenByDescending(alias => alias.UpdatedAtUtc)
                        .First()
            );

        foreach (var alias in existing)
        {
            if (!requestedKeys.Contains(new AliasKey(alias.Kind, alias.NormalizedValue)))
            {
                alias.DisabledAtUtc ??= now;
                alias.UpdatedAtUtc = now;
            }
        }

        foreach (var alias in aliasWrites)
        {
            var key = new AliasKey(alias.Kind, alias.NormalizedValue);
            if (existingByKey.TryGetValue(key, out var existingAlias))
            {
                existingAlias.DisplayValue = alias.DisplayValue;
                existingAlias.DisabledAtUtc = null;
                existingAlias.UpdatedAtUtc = now;
                continue;
            }

            db.UserAliases.Add(
                new UserAlias
                {
                    Id = "alias_" + Guid.NewGuid().ToString("N"),
                    OrganizationId = organizationId,
                    UserId = userId,
                    Kind = alias.Kind,
                    DisplayValue = alias.DisplayValue,
                    NormalizedValue = alias.NormalizedValue,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }
            );
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUserAliasUniqueViolation(exception))
        {
            throw new InvalidOperationException(
                "An alias already belongs to another member in this organization.",
                exception
            );
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await ListUserAliasesAsync(organizationId, userId, cancellationToken);
    }

    private readonly record struct AliasKey(UserAliasKind Kind, string NormalizedValue);

    private static UserAliasWrite ValidateAliasWrite(UserAliasWrite alias)
    {
        var expectedDisplayValue = alias.Kind switch
        {
            UserAliasKind.Email => alias.DisplayValue.Trim(),
            UserAliasKind.GitHub => alias.DisplayValue.Trim().TrimStart('@'),
            _ => throw new ArgumentOutOfRangeException(
                nameof(alias),
                alias.Kind,
                "Unsupported user alias kind."
            ),
        };
        var expectedNormalizedValue = expectedDisplayValue.ToLowerInvariant();

        if (!string.Equals(alias.DisplayValue, expectedDisplayValue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Alias display value must match the normalized display form.",
                nameof(alias)
            );
        }

        if (
            !string.Equals(alias.NormalizedValue, expectedNormalizedValue, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "Alias normalized value must match the display value.",
                nameof(alias)
            );
        }

        return alias;
    }

    private async ValueTask<IDbContextTransaction?> BeginTransactionIfNeededAsync(
        CancellationToken cancellationToken
    ) =>
        db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private async Task<OrganizationMembership?> CreateSameDomainInvitationIfEligibleAsync(
        string personalOrganizationId,
        string userId,
        string? email,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var domain = EmailDomainNormalizer.FromEmail(email);
        if (domain is null || PublicEmailDomainCatalog.IsPublicEmailDomain(domain))
        {
            return null;
        }

        var candidate = await db
            .Organizations.TagWithOperationCallSite("identity.same_domain.find_candidate")
            .AsNoTracking()
            .Where(organization =>
                organization.Id != personalOrganizationId
                && organization.AutoInviteSameDomainEnabled
                && organization.AutoInviteSameDomain == domain
                && organization.DisabledAtUtc == null
            )
            .Select(organization => new
            {
                organization.Id,
                organization.CreatedByUserId,
                organization.AutoInviteDefaultRole,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (candidate is null || email is null)
        {
            return null;
        }

        var invitationEmail = email.Trim().ToLowerInvariant();

        var alreadyActive = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "identity.same_domain.check_existing_membership"
            )
            .AsNoTracking()
            .AnyAsync(
                membership =>
                    membership.OrganizationId == candidate.Id
                    && membership.UserId == userId
                    && membership.Status == MembershipStatus.Active
                    && membership.DisabledAtUtc == null,
                cancellationToken
            );

        if (alreadyActive)
        {
            return null;
        }

        await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "identity.same_domain.retire_expired_invitation"
            )
            .Where(membership =>
                membership.OrganizationId == candidate.Id
                && membership.InvitedEmail == invitationEmail
                && membership.IsSameDomainAutoInvite
                && membership.Status == MembershipStatus.Pending
                && membership.DisabledAtUtc == null
                && membership.ExpiresAtUtc <= now
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(membership => membership.Status, MembershipStatus.Declined)
                        .SetProperty(membership => membership.DisabledAtUtc, now),
                cancellationToken
            );

        var pendingExists = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "identity.same_domain.check_existing_invitation"
            )
            .AsNoTracking()
            .AnyAsync(
                membership =>
                    membership.OrganizationId == candidate.Id
                    && membership.InvitedEmail == invitationEmail
                    && membership.IsSameDomainAutoInvite
                    && membership.Status == MembershipStatus.Pending
                    && membership.DisabledAtUtc == null
                    && membership.ExpiresAtUtc > now,
                cancellationToken
            );

        if (pendingExists)
        {
            return null;
        }

        var invitation = new OrganizationMembership
        {
            Id = "mem_" + Guid.NewGuid().ToString("N"),
            OrganizationId = candidate.Id,
            UserId = null,
            Role = NormalizeAutoInviteRole(candidate.AutoInviteDefaultRole),
            Status = MembershipStatus.Pending,
            InvitedEmail = invitationEmail,
            IsSameDomainAutoInvite = true,
            CreatedByUserId = candidate.CreatedByUserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7),
        };
        db.OrganizationMemberships.Add(invitation);

        return invitation;
    }

    private static string NormalizeAutoInviteRole(string role) =>
        role is "admin" ? "admin" : "member";

    private static bool IsSameDomainAutoInviteUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: SameDomainAutoInviteIndexName,
            };

    private static bool IsUserAliasUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: UserAliasUniqueIndexName,
            };

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

    /// <inheritdoc />
    public async Task<int> RevokeUserTokensForOrganizationMemberAsync(
        string organizationId,
        string ownerUserId,
        DateTimeOffset revokedAtUtc,
        CancellationToken ct
    ) =>
        await db
            .UserTokens.TagWithOperationCallSite(
                "identity.user_token.revoke_for_organization_member"
            )
            .Where(token =>
                token.OrganizationId == organizationId
                && token.OwnerUserId == ownerUserId
                && token.RevokedAtUtc == null
            )
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAtUtc, revokedAtUtc),
                ct
            );

    private async Task<AuthContext> FindActiveContextAsync(
        string userId,
        CancellationToken cancellationToken
    )
    {
        var activatedContext = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "identity.auth_context.find_activated_org_membership"
            )
            .AsNoTracking()
            .Where(membership =>
                membership.UserId == userId
                && membership.Status == MembershipStatus.Active
                && membership.DisabledAtUtc == null
            )
            .Join(
                db.Organizations.TagWithOperationCallSite(
                        "identity.auth_context.find_activated_org"
                    )
                    .AsNoTracking()
                    .Where(organization =>
                        organization.ActivatedAtUtc != null && organization.DisabledAtUtc == null
                    ),
                membership => membership.OrganizationId,
                organization => organization.Id,
                (membership, organization) =>
                    new { OrganizationMembership = membership, organization }
            )
            .Join(
                db.Teams.TagWithOperationCallSite("identity.auth_context.find_activated_root_team")
                    .AsNoTracking()
                    .Where(team => team.IsRootTeam && team.DisabledAtUtc == null),
                joined => joined.OrganizationMembership.OrganizationId,
                team => team.OrganizationId,
                (joined, team) => new { joined.OrganizationMembership, Team = team }
            )
            .Join(
                db.TeamMemberships.TagWithOperationCallSite(
                        "identity.auth_context.find_activated_root_team_membership"
                    )
                    .AsNoTracking()
                    .Where(membership =>
                        membership.UserId == userId && membership.DisabledAtUtc == null
                    ),
                joined => new
                {
                    joined.OrganizationMembership.OrganizationId,
                    TeamId = joined.Team.Id,
                },
                teamMembership => new { teamMembership.OrganizationId, teamMembership.TeamId },
                (joined, teamMembership) =>
                    new { joined.OrganizationMembership, TeamMembership = teamMembership }
            )
            .OrderByDescending(joined => joined.OrganizationMembership.IsDefault)
            .ThenBy(joined => joined.OrganizationMembership.CreatedAtUtc)
            .ThenBy(joined => joined.TeamMembership.CreatedAtUtc)
            .ThenBy(joined => joined.OrganizationMembership.OrganizationId)
            .ThenBy(joined => joined.TeamMembership.TeamId)
            .Select(joined => new AuthContext(
                userId,
                joined.OrganizationMembership.OrganizationId,
                joined.TeamMembership.TeamId
            ))
            .FirstOrDefaultAsync(cancellationToken);

        if (activatedContext is not null)
        {
            return activatedContext;
        }

        var fallbackContext = await db
            .OrganizationMemberships.TagWithOperationCallSite(
                "identity.auth_context.find_fallback_org_membership"
            )
            .AsNoTracking()
            .Where(membership =>
                membership.UserId == userId
                && membership.Status == MembershipStatus.Active
                && membership.DisabledAtUtc == null
            )
            .Join(
                db.Organizations.TagWithOperationCallSite("identity.auth_context.find_fallback_org")
                    .AsNoTracking()
                    .Where(organization =>
                        organization.ActivatedAtUtc != null && organization.DisabledAtUtc == null
                    ),
                membership => membership.OrganizationId,
                organization => organization.Id,
                (membership, organization) => membership.OrganizationId
            )
            .Join(
                db.TeamMemberships.TagWithOperationCallSite(
                        "identity.auth_context.find_fallback_team"
                    )
                    .AsNoTracking()
                    .Where(membership =>
                        membership.UserId == userId && membership.DisabledAtUtc == null
                    ),
                organizationId => organizationId,
                teamMembership => teamMembership.OrganizationId,
                (organizationId, teamMembership) => teamMembership
            )
            .OrderBy(teamMembership => teamMembership.CreatedAtUtc)
            .Select(teamMembership => new AuthContext(
                userId,
                teamMembership.OrganizationId,
                teamMembership.TeamId
            ))
            .FirstAsync(cancellationToken);

        return fallbackContext;
    }
}
