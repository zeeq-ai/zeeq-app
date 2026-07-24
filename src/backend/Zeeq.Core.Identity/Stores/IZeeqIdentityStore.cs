using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Resolved owner context for binding credentials and tokens to a local identity.
/// </summary>
/// <remarks>
/// Populated after a user is created or located via <see cref="IZeeqIdentityStore.EnsureUserAsync"/>.
/// All credential and token rows store this context at creation time so that owner identity
/// is always derived from server-side metadata — never from token request parameters.
/// <para>
/// <c>PartitionIdsJson</c> is the JSON-serialised list of partition IDs the owner belongs to.
/// It is embedded in access tokens so that the MCP resource layer can enforce data isolation
/// without additional database round-trips.
/// </para>
/// </remarks>
/// <param name="UserId">Local user ID that owns the credential or token.</param>
/// <param name="OrganizationId">Default organization ID resolved for the local user.</param>
/// <param name="TeamId">Default team ID resolved for the local user.</param>
/// <param name="PartitionIdsJson">JSON-serialised partition IDs available to the owner.</param>
/// <param name="Provider">External IdP identifier used to resolve the local identity.</param>
/// <param name="ProviderSubject">Subject claim from the external IdP for the owner.</param>
public sealed record OwnerContext(
    string UserId,
    string OrganizationId,
    string TeamId,
    string PartitionIdsJson,
    string Provider,
    string ProviderSubject
);

/// <summary>
/// Validated alias value to persist for a user in one organization.
/// </summary>
/// <param name="Kind">Alias namespace.</param>
/// <param name="DisplayValue">User-facing value after trimming.</param>
/// <param name="NormalizedValue">Canonical lookup value.</param>
public sealed record UserAliasWrite(
    UserAliasKind Kind,
    string DisplayValue,
    string NormalizedValue
);

/// <summary>
/// Persistence contract for all local identity artifacts: users, DCR client setups,
/// client credentials, and long-lived user tokens.
/// </summary>
/// <remarks>
/// Human identity is owned by an external IdP (Google, Office 365, mock OAuth2, etc.).
/// This store persists the <em>local</em> representations that are derived from a verified
/// upstream identity: app-owned users/orgs/teams, OAuth client registrations, and bearer
/// token metadata. It never stores plaintext passwords or upstream IdP secrets.
/// <para>
/// Callers must not expose stored client secrets through list endpoints or server logs.
/// The create response may show a generated secret once; after that, only a hash or
/// revocation record should be retained.
/// </para>
/// </remarks>
public interface IZeeqIdentityStore
{
    /// <summary>
    /// Resolves or creates the local user that corresponds to the given external IdP identity.
    /// </summary>
    /// <param name="provider">External IdP identifier, e.g. <c>google</c> or <c>mock</c>.</param>
    /// <param name="providerSubject">The <c>sub</c> claim issued by the external IdP. Combined
    /// with <paramref name="provider"/> this pair is the unique external identity key.</param>
    /// <param name="displayName">Optional display name from the upstream IdP.</param>
    /// <param name="email">Optional verified email from the upstream IdP.</param>
    /// <param name="pictureUrl">Optional photo/avatar URL from the upstream IdP.</param>
    /// <param name="cancellationToken"/>
    /// <returns>
    /// An <see cref="AuthContext"/> carrying the local <c>UserId</c>, <c>OrganizationId</c>,
    /// <c>TeamId</c>, and partition list. A new user, initial organization, and initial team
    /// are provisioned on first call; subsequent calls for the same <c>(provider, providerSubject)</c>
    /// pair return the existing records.
    /// </returns>
    /// <remarks>
    /// Existing disabled users or disabled identities must fail closed: if either the
    /// <c>AuthUser</c> or the <c>AuthUserIdentity</c> row is disabled this method must throw
    /// rather than returning a context that would allow login to proceed.
    /// </remarks>
    Task<AuthContext> EnsureUserAsync(
        string provider,
        string providerSubject,
        string? displayName,
        string? email,
        string? pictureUrl,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Resolves a user's current email by local user id.
    /// </summary>
    /// <param name="userId">Local user ID (the OpenIddict <c>sub</c> for user-owned tokens).</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>The user's email, or <see langword="null"/> if the user or its email is unset.</returns>
    /// <remarks>
    /// Used as a fallback when a request's authenticated principal has no <c>email</c>
    /// claim on the token itself (e.g. some machine-credential flows), so callers can
    /// still attribute the request to a real email without trusting client input.
    /// </remarks>
    Task<string?> FindUserEmailAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists aliases owned by one user in one organization.
    /// </summary>
    Task<IReadOnlyList<UserAlias>> ListUserAliasesAsync(
        string organizationId,
        string userId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces aliases owned by one user in one organization.
    /// </summary>
    Task<IReadOnlyList<UserAlias>> ReplaceUserAliasesAsync(
        string organizationId,
        string userId,
        IReadOnlyList<UserAliasWrite> aliases,
        CancellationToken cancellationToken
    );

    // ── Dynamic Client Registration ────────────────────────────────────────────

    /// <summary>
    /// Persists a new DCR setup row in the <c>pending_login</c> state.
    /// </summary>
    /// <remarks>
    /// The row is created when an MCP client completes Dynamic Client Registration
    /// (RFC 7591) at <c>/connect/register</c> before the user has logged in.
    /// The corresponding OpenIddict application is registered at the same time;
    /// token issuance is blocked until the setup is claimed via
    /// <see cref="ClaimDcrSetupAsync"/>.
    /// <para>
    /// Abandoned pending rows must be treated as bounded retry artifacts:
    /// they are not standalone public registrations and must be cleaned up or
    /// expired on a schedule.
    /// </para>
    /// </remarks>
    /// <param name="setup">Pending DCR setup row created from the client registration request.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    Task CreatePendingDcrSetupAsync(DcrClientSetup setup, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the <see cref="DcrClientSetup"/> for the given <paramref name="clientId"/>,
    /// or <see langword="null"/> if no row exists.
    /// </summary>
    /// <param name="clientId">OAuth client ID issued during Dynamic Client Registration.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>
    /// The matching DCR setup row; <see langword="null"/> if no setup exists for the client.
    /// </returns>
    Task<DcrClientSetup?> FindDcrSetupAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>
    /// Transitions the DCR setup row for <paramref name="clientId"/> to the
    /// <c>expired</c> state.
    /// </summary>
    /// <param name="clientId">OAuth client ID whose pending DCR setup should expire.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <remarks>
    /// Called by the authorization endpoint when the setup TTL has elapsed or when
    /// a superseding registration is accepted. After expiry, <c>/connect/token</c>
    /// must reject authorization-code exchanges with <c>invalid_grant</c>.
    /// </remarks>
    Task MarkDcrSetupExpiredAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically assigns owner identity to a pending DCR setup and transitions it
    /// to the <c>active</c> state.
    /// </summary>
    /// <param name="clientId">The client that was registered via DCR.</param>
    /// <param name="owner">Resolved owner context obtained after the user authenticates
    /// through the external IdP.</param>
    /// <param name="cancellationToken"/>
    /// <remarks>
    /// This is the convergence point for the two DCR claim flows:
    /// <list type="bullet">
    ///   <item>DCR first, then login — the MCP client registers before the user logs in;
    ///   the authorization endpoint triggers the claim after cookie login completes.</item>
    ///   <item>Login first, then DCR — the user already has a browser session when DCR
    ///   runs; the claim happens immediately during registration.</item>
    /// </list>
    /// The implementation must use a concurrency-safe conditional update (e.g. optimistic
    /// concurrency or a single UPDATE … WHERE status = 'pending_login') so that two
    /// concurrent claim attempts do not both succeed.
    /// </remarks>
    Task ClaimDcrSetupAsync(
        string clientId,
        OwnerContext owner,
        CancellationToken cancellationToken
    );

    // ── Client Credentials ────────────────────────────────────────────────────

    /// <summary>
    /// Returns client credentials owned by <paramref name="ownerUserId"/>.
    /// </summary>
    /// <param name="ownerUserId">Local user ID that owns the credential rows.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>Client credential rows owned by the user.</returns>
    /// <remarks>
    /// The returned records must never include plaintext client secrets.
    /// </remarks>
    Task<IReadOnlyList<ClientCredential>> ListClientCredentialsAsync(
        string ownerUserId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Persists a new client credential row.
    /// </summary>
    /// <param name="credential">Client credential metadata to persist for the owner.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <remarks>
    /// Must be called after the corresponding confidential OpenIddict application has
    /// been registered. The <see cref="ClientCredential.ClientSecret"/> stored here is
    /// for PoC use only; production implementations should store only a hash.
    /// </remarks>
    Task AddClientCredentialAsync(ClientCredential credential, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up a client credential by its OAuth <paramref name="clientId"/>.
    /// Returns <see langword="null"/> if the credential does not exist or has been revoked.
    /// </summary>
    /// <param name="clientId">OAuth client ID to resolve to local credential ownership metadata.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>
    /// The active credential row; <see langword="null"/> if the client is unknown or revoked.
    /// </returns>
    /// <remarks>
    /// Called by the token endpoint grant handler to verify that the validated
    /// OpenIddict client has a matching local ownership row before signing in a principal.
    /// </remarks>
    Task<ClientCredential?> FindClientCredentialAsync(
        string clientId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes the credential identified by <paramref name="clientId"/> if it is owned by
    /// <paramref name="ownerUserId"/>.
    /// </summary>
    /// <param name="clientId">OAuth client ID whose credential should be revoked.</param>
    /// <param name="ownerUserId">Local user ID expected to own the credential.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>
    /// <see langword="true"/> if the row was found and deleted; <see langword="false"/> if
    /// no matching row exists or the caller does not own it.
    /// </returns>
    /// <remarks>
    /// Deleting the local row blocks future token issuance. Already-issued self-contained
    /// access tokens remain valid until their individual expiry time; callers must account
    /// for this residual window.
    /// </remarks>
    Task<bool> DeleteClientCredentialAsync(
        string clientId,
        string ownerUserId,
        CancellationToken cancellationToken
    );

    // ── User Tokens ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all user token metadata rows owned by <paramref name="ownerUserId"/>.
    /// </summary>
    /// <param name="ownerUserId">Local user ID that owns the token metadata rows.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>User token metadata rows owned by the user.</returns>
    /// <remarks>
    /// These are metadata-only records; the plaintext bearer token is never stored.
    /// The list includes active, expired, and revoked entries so that the UI can show
    /// the full token history.
    /// </remarks>
    Task<IReadOnlyList<UserToken>> ListUserTokensAsync(
        string ownerUserId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Persists a new user token metadata row.
    /// </summary>
    /// <param name="token">User token metadata to persist after token issuance.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <remarks>
    /// Must be called after the token has been issued by the OpenIddict token endpoint
    /// via the custom <c>urn:mcp:grant-type:user_token</c> grant. The row carries the
    /// token's <c>Id</c> claim so that <see cref="FindUserTokenAsync"/> and
    /// <see cref="MarkUserTokenUsedAsync"/> can validate and audit subsequent uses.
    /// </remarks>
    Task AddUserTokenAsync(UserToken token, CancellationToken cancellationToken);

    /// <summary>
    /// Hard-deletes the token metadata row identified by <paramref name="tokenId"/>.
    /// </summary>
    /// <param name="tokenId">Token metadata row ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <remarks>
    /// Used to roll back failed token issuance before a value is returned to the user.
    /// User-initiated deletes use <see cref="DeleteUserTokenAsync"/> so ownership is checked.
    /// </remarks>
    Task RemoveUserTokenAsync(string tokenId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the metadata row for <paramref name="tokenId"/>, or <see langword="null"/>
    /// if it does not exist.
    /// </summary>
    /// <param name="tokenId">Token ID from the validated bearer token claim.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>
    /// The token metadata row; <see langword="null"/> if the token ID is unknown.
    /// </returns>
    /// <remarks>
    /// Called by the validation middleware after OpenIddict has verified the token
    /// cryptographically. A missing or revoked row must cause the middleware to reject
    /// the request with <c>401 Unauthorized</c>.
    /// </remarks>
    Task<UserToken?> FindUserTokenAsync(string tokenId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the token identified by <paramref name="tokenId"/> if it is owned by
    /// <paramref name="ownerUserId"/>.
    /// </summary>
    /// <param name="tokenId">Token metadata row ID to delete.</param>
    /// <param name="ownerUserId">Local user ID expected to own the token.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>
    /// <see langword="true"/> if the row was found and deleted; <see langword="false"/> if
    /// no matching row exists or the caller does not own it.
    /// </returns>
    /// <remarks>
    /// Long-lived bearer tokens are self-contained JWTs. Deletion is enforced by the
    /// <c>UserTokenValidationMiddleware</c> because missing metadata rows are rejected
    /// on every request. There is no push-based revocation mechanism; all enforcement is
    /// pull-based via this store.
    /// </remarks>
    Task<bool> DeleteUserTokenAsync(
        string tokenId,
        string ownerUserId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Records that a token was successfully used, updating its <c>LastUsedAtUtc</c>
    /// timestamp.
    /// </summary>
    /// <param name="tokenId">Token metadata row ID to mark as used.</param>
    /// <param name="ownerUserId">Local user ID expected to own the token.</param>
    /// <param name="usedAtUtc">Request timestamp to store as the token's last-used time.</param>
    /// <param name="cancellationToken">Cancellation token for the database operation.</param>
    /// <returns>
    /// <see langword="true"/> if the row was found and updated; <see langword="false"/> if
    /// no matching row exists or the caller does not own it.
    /// </returns>
    /// <remarks>
    /// Called by the validation middleware on each authenticated MCP request. The
    /// <paramref name="usedAtUtc"/> value should be the request timestamp, not
    /// <c>DateTimeOffset.UtcNow</c>, so that audit records are consistent with
    /// upstream telemetry. Production implementations may want to batch or debounce
    /// these writes to avoid high-frequency database updates on busy tokens.
    /// </remarks>
    Task<bool> MarkUserTokenUsedAsync(
        string tokenId,
        string ownerUserId,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Revokes all of a user's active long-lived tokens scoped to an organization.
    /// </summary>
    /// <param name="organizationId">Organization the caller was removed from or left.</param>
    /// <param name="ownerUserId">Local user ID whose organization-scoped tokens should be revoked.</param>
    /// <param name="revokedAtUtc">Revocation timestamp to stamp on affected rows.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>Number of token rows revoked.</returns>
    /// <remarks>
    /// Called from membership removal/leave flows so a token issued while the
    /// user was a member cannot outlive their access to the organization. This
    /// is a bulk, set-based update — it does not enumerate <see cref="UserToken"/>
    /// rows individually.
    /// </remarks>
    Task<int> RevokeUserTokensForOrganizationMemberAsync(
        string organizationId,
        string ownerUserId,
        DateTimeOffset revokedAtUtc,
        CancellationToken ct
    );
}
