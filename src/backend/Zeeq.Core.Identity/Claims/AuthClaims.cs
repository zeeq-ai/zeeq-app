namespace Zeeq.Core.Identity;

/// <summary>
/// Claim names used to preserve the external IdP identity inside local cookies
/// and OpenIddict-issued access tokens.
/// </summary>
public static class AuthClaims
{
    /// <summary>
    /// Local organization selected for the current browser/API/MCP session.
    /// </summary>
    /// <remarks>
    /// Originates from <see cref="AuthUserStore" /> during external IdP login and from
    /// persisted ownership metadata when issuing client-credential or long-lived user tokens.
    /// It is an opaque tenant/routing key, not an organization display name.
    /// </remarks>
    public const string OrganizationId = "org_id";

    /// <summary>
    /// Local team selected inside <see cref="OrganizationId" /> for the current session.
    /// </summary>
    /// <remarks>
    /// Originates from the active team membership resolved at login, or from persisted
    /// credential/token ownership metadata. It must be interpreted together with
    /// <see cref="OrganizationId" /> because teams are scoped to organizations.
    /// </remarks>
    public const string TeamId = "team_id";

    /// <summary>
    /// JSON array of selected MCP content partition identifiers.
    /// </summary>
    /// <remarks>
    /// Originates from login or credential setup selection. The current PoC emits an
    /// empty JSON array. Empty means no partitions selected; it must not be treated as
    /// access to all partitions.
    /// </remarks>
    public const string PartitionIds = "partition_ids";

    /// <summary>
    /// External identity provider that authenticated the local user.
    /// </summary>
    /// <remarks>
    /// Originates from the configured external provider name, such as <c>mock</c> or
    /// <c>google</c>. This is audit metadata for tracing the upstream login source.
    /// Authorization decisions should use the local user/org/team claims instead.
    /// </remarks>
    public const string Provider = "provider";

    /// <summary>
    /// Subject identifier assigned by the external identity provider.
    /// </summary>
    /// <remarks>
    /// Originates from the provider <c>id_token</c> or userinfo <c>sub</c> claim. Together
    /// with <see cref="Provider" />, it identifies the linked external identity, but it is
    /// not the local OpenIddict subject for app-issued tokens.
    /// </remarks>
    public const string ProviderSubject = "provider_sub";

    /// <summary>
    /// Photo/avatar URL from the external IdP profile.
    /// </summary>
    /// <remarks>
    /// Originates from the OIDC <c>picture</c> claim (Google) or the
    /// <c>avatar_url</c> field (GitHub). Synced on each login.
    /// </remarks>
    public const string Picture = "picture";

    /// <summary>
    /// URL-safe slug for the current organization.
    /// </summary>
    /// <remarks>
    /// Set at login from the active organization. Used by the UI for
    /// org-scoped routing without an extra API call.
    /// </remarks>
    public const string OrganizationSlug = "org_slug";

    /// <summary>
    /// Local issuance mode for an OpenIddict access token.
    /// </summary>
    /// <remarks>
    /// Originates from the token factory/handler that created the principal. Current values
    /// distinguish client credentials from long-lived user-token issuance for diagnostics
    /// and downstream policy checks.
    /// </remarks>
    public const string AuthMode = "auth_mode";

    /// <summary>
    /// Local user that owns a machine credential when the token subject is not the user.
    /// </summary>
    /// <remarks>
    /// Originates from <see cref="Zeeq.Core.Models.ClientCredential.OwnerUserId" /> when issuing
    /// client-credential access tokens. In that flow, the OAuth <c>sub</c> is the client ID,
    /// so this claim preserves the local owning user separately.
    /// </remarks>
    public const string OwnerUserId = "owner_user_id";

    /// <summary>
    /// Local client credential identifier embedded in client-credentials tokens.
    /// </summary>
    /// <remarks>
    /// Originates from <see cref="Zeeq.Core.Models.ClientCredential.ClientId" />. It matches the
    /// OpenIddict client ID and is used for diagnostics and future credential-level policy
    /// checks.
    /// </remarks>
    public const string ClientCredentialId = "auth_client_credential_id";

    /// <summary>
    /// User-facing display name for the client credential that issued the token.
    /// </summary>
    /// <remarks>
    /// Originates from <see cref="Zeeq.Core.Models.ClientCredential.DisplayName" />. This is
    /// convenience metadata for diagnostics/UI and should not be used as a stable identifier.
    /// </remarks>
    public const string ClientCredentialName = "auth_client_credential_name";

    /// <summary>
    /// Local metadata row identifier for a long-lived user token.
    /// </summary>
    /// <remarks>
    /// Originates from <see cref="Zeeq.Core.Models.UserToken.Id" />. Middleware uses this claim
    /// after OpenIddict validation to enforce local revocation and last-used tracking.
    /// </remarks>
    public const string UserTokenId = "auth_user_token_id";

    /// <summary>
    /// User-facing display name for the long-lived user token.
    /// </summary>
    /// <remarks>
    /// Originates from <see cref="Zeeq.Core.Models.UserToken.DisplayName" />. This is diagnostic
    /// metadata only and should not be used for authorization or lookup.
    /// </remarks>
    public const string UserTokenName = "auth_user_token_name";
}
