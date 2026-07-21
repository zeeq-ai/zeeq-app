namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Authentication related configuration settings.
    /// </summary>
    public AuthSettings Auth { get; init; } = new();
}

/// <summary>
/// Local authentication settings for the OpenIddict application.
/// </summary>
/// <remarks>
/// The external provider creates the human identity. OpenIddict consumes the
/// verified upstream identity and issues local cookies, authorization codes, and
/// access tokens for this app and its MCP resource.
/// </remarks>
public sealed record AuthSettings
{
    /// <summary>
    /// Public issuer base URI for this app's OpenIddict authorization server.
    /// </summary>
    /// <remarks>
    /// This value is advertised in OpenID Connect discovery, used as the issuer
    /// on locally issued tokens, and used to build authorization/token/JWKS
    /// endpoint URLs. In local Aspire runs this is the external YARP listener
    /// at <c>http://localhost:8091</c>; production should set the public HTTPS
    /// origin clients use.
    /// </remarks>
    public string Issuer { get; set; } = "http://localhost:8091";

    /// <summary>
    /// OAuth resource identifier for the MCP endpoint protected by this server.
    /// </summary>
    /// <remarks>
    /// MCP clients send this value as the RFC 8707 resource indicator and the
    /// C# MCP authentication middleware advertises it in protected-resource
    /// metadata. It intentionally points to the MCP resource URL, not the browser
    /// app origin.
    /// </remarks>
    public string Resource { get; set; } = "http://localhost:8091/mcp";

    /// <summary>
    /// Browser application origin that should receive local app cookies.
    /// </summary>
    /// <remarks>
    /// Used to validate post-login return URLs and to decide when an OAuth callback
    /// needs browser handoff. In local development, Google callbacks arrive through
    /// <see cref="Issuer" /> on localhost, then hand off to the Vue app origin on
    /// <c>mcp-app.localhost</c> so cookies are scoped to the browser UI.
    /// </remarks>
    public string FrontendBaseUri { get; set; } = "http://mcp-app.localhost:8090";

    /// <summary>
    /// Issuer URI for the local OAuth2 mock provider used by Aspire development.
    /// </summary>
    /// <remarks>
    /// This is not the app's local OpenIddict issuer. It is the external IdP that
    /// simulates Google/O365-style human authentication for local testing.
    /// </remarks>
    public string MockProviderIssuer { get; set; } = "http://localhost:9322";

    /// <summary>
    /// Stable provider key for the local mock external IdP.
    /// </summary>
    /// <remarks>
    /// This provider key is stored as audit metadata in local auth claims and rows.
    /// The catalog currently registers the mock provider under <c>mock</c> so tests
    /// and local flows have a provider even when no real IdP is configured.
    /// </remarks>
    public string MockProviderName { get; set; } = "mock";

    /// <summary>
    /// OAuth client ID used when this application talks to the mock provider.
    /// </summary>
    /// <remarks>
    /// The mock provider accepts this local value. Real providers use
    /// <see cref="ProviderAuthSettings.ClientId" /> from configured provider entries.
    /// </remarks>
    public string MockClientId { get; set; } = "mcp-openiddict-local";

    /// <summary>
    /// Server callback URI registered for the local mock provider.
    /// </summary>
    /// <remarks>
    /// The mock provider can callback directly to the app. Other providers, such as
    /// Google, use <see cref="ProviderAuthSettings.ServerCallbackUri" /> because their
    /// callback host requirements differ from the local mock flow.
    /// </remarks>
    public string MockCallbackUri { get; set; } = "http://localhost:8091/auth/callback/mock";

    /// <summary>
    /// Maximum age for one-time external-login state and PKCE verifier records.
    /// </summary>
    /// <remarks>
    /// This protects the browser login initiation step against stale or replayed
    /// callback state. The current implementation stores this state in memory; the
    /// production plan replaces it with a distributed one-time state store.
    /// </remarks>
    public TimeSpan LoginStateLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Access token serialization mode for locally issued OpenIddict tokens.
    /// </summary>
    /// <remarks>
    /// Defaults to encrypted compact JWE. Signed-only JWT is available for clients
    /// and diagnostics that need readable token bodies, but it exposes claims to
    /// token holders and should be a deliberate environment choice.
    /// </remarks>
    public AccessTokenFormat AccessTokenFormat { get; set; } = AccessTokenFormat.EncryptedJwt;

    /// <summary>
    /// OAuth client ID for the server-owned custom grant used to mint long-lived user tokens.
    /// </summary>
    /// <remarks>
    /// Browser-authenticated users create long-lived tokens through the management API.
    /// That API stores local token metadata and then calls the OpenIddict token endpoint
    /// with a short-lived one-time ticket. This confidential client authenticates that
    /// back-channel call so the server does not need <c>AcceptAnonymousClients()</c>.
    /// </remarks>
    public string InternalUserTokenClientId { get; set; } = "auth_internal_user_token_issuer";

    /// <summary>
    /// OAuth client secret for the internal long-lived-token issuer client.
    /// </summary>
    /// <remarks>
    /// Configure this from user secrets, Aspire parameters, environment variables, or a
    /// mounted secret file in real deployments. The default exists only so the local
    /// Aspire demo can bootstrap without an extra secret store; production should
    /// override it through <c>AppSettings:Auth:InternalUserTokenClientSecret</c> or
    /// equivalent secret configuration.
    /// </remarks>
    public string InternalUserTokenClientSecret { get; set; } =
        "local-development-internal-user-token-secret";

    /// <summary>
    /// Lifetime for authorization codes issued to DCR/native MCP clients.
    /// </summary>
    /// <remarks>
    /// Authorization codes are front-channel artifacts and should be short-lived.
    /// They are exchanged immediately by the coding client after the user completes
    /// browser login.
    /// </remarks>
    public TimeSpan AuthorizationCodeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default access-token lifetime for interactive DCR authorization-code clients.
    /// </summary>
    /// <remarks>
    /// Client-credentials and long-lived user-token flows override this at the
    /// application or principal level. This value is intentionally short because
    /// interactive MCP clients can request refresh tokens with <c>offline_access</c>.
    /// </remarks>
    public TimeSpan InteractiveAccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Access-token lifetime for user-owned client ID/client secret credentials.
    /// </summary>
    /// <remarks>
    /// Client-credentials clients do not receive refresh tokens; they refresh by
    /// authenticating to the token endpoint again. Keeping these tokens short limits
    /// the exposure window if a bearer token leaks.
    /// </remarks>
    public TimeSpan ClientCredentialsAccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Refresh-token lifetime for interactive MCP authorization-code clients.
    /// </summary>
    /// <remarks>
    /// The browser app keeps using its local app cookie. Refresh tokens are only for
    /// setup-bound DCR clients that request <c>offline_access</c>.
    /// </remarks>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Clock-skew leeway used when reusing or rotating refresh tokens.
    /// </summary>
    public TimeSpan RefreshTokenReuseLeeway { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default requested lifetime for browser-created long-lived user tokens.
    /// </summary>
    /// <remarks>
    /// Used when the management API request omits an explicit lifetime. These tokens
    /// are still issued by OpenIddict and checked against local metadata for revocation.
    /// </remarks>
    public int UserTokenDefaultLifetimeDays { get; set; } = 365;

    /// <summary>
    /// Minimum allowed lifetime for browser-created long-lived user tokens.
    /// </summary>
    /// <remarks>
    /// Keeps generated credentials aligned with the settings UI slider and avoids
    /// very short-lived credentials that are better served by interactive auth.
    /// </remarks>
    public int UserTokenMinLifetimeDays { get; set; } = 30;

    /// <summary>
    /// Maximum allowed lifetime for browser-created long-lived user tokens.
    /// </summary>
    /// <remarks>
    /// Caps user input from the token management API. This is a risk-control default,
    /// not a replacement for future org policy, audit, and rotation controls.
    /// </remarks>
    public int UserTokenMaxLifetimeDays { get; set; } = 730;

    /// <summary>
    /// <see cref="Issuer" /> without a trailing slash for consistent URL composition.
    /// </summary>
    public string IssuerTrimmed => Issuer.TrimEnd('/');

    /// <summary>
    /// <see cref="Issuer" /> guaranteed to end with a single trailing slash,
    /// matching the <c>iss</c> values advertised in discovery metadata, JWT claims,
    /// and RFC 9207 response parameters.
    /// </summary>
    public string IssuerNormalized => IssuerTrimmed + "/";

    /// <summary>
    /// <see cref="Resource" /> without a trailing slash for resource comparison.
    /// </summary>
    public string ResourceTrimmed => Resource.TrimEnd('/');

    /// <summary>
    /// <see cref="FrontendBaseUri" /> without a trailing slash for return URL checks.
    /// </summary>
    public string FrontendBaseUriTrimmed => FrontendBaseUri.TrimEnd('/');

    /// <summary>
    /// <see cref="MockProviderIssuer" /> without a trailing slash for discovery URL composition.
    /// </summary>
    public string MockProviderIssuerTrimmed => MockProviderIssuer.TrimEnd('/');

    /// <summary>
    /// The configured external login providers available to the auth UI and login endpoints.
    /// </summary>
    public ProviderAuthSettings[] Providers { get; set; } = [];

    /// <summary>
    /// Redirect URI allow-list used when validating Dynamic Client Registration requests.
    /// </summary>
    public DynamicClientRegistrationSettings DynamicClientRegistration { get; set; } = new();
}

/// <summary>
/// Allow-list configuration for redirect URIs accepted during Dynamic Client Registration.
/// </summary>
/// <remarks>
/// See `DynamicClientRegistrationValidator`. HTTPS redirect URIs are always
/// allowed for any host; this configuration controls only loopback HTTP hosts and the
/// fixed non-loopback callback URIs used by known native clients such as Cursor.
/// </remarks>
public sealed record DynamicClientRegistrationSettings
{
    /// <summary>
    /// Loopback hostnames allowed for native client HTTP redirect URIs.
    /// </summary>
    public string[] AllowedLoopbackHosts { get; set; } = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    /// Fixed non-loopback redirect URIs allowed for known native MCP clients.
    /// </summary>
    /// <remarks>
    /// Cursor uses a private URI scheme callback instead of a loopback HTTP redirect.
    /// Matching ignores the query string, consistent with the loopback check.
    /// </remarks>
    public string[] AllowedStaticRedirectUris { get; set; } =
    [
        "cursor://anysphere.cursor-mcp/oauth/callback",
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.ai/api/auth/callback",
    ];
}

/// <summary>
/// Configuration for one external OAuth/OIDC provider used for human login.
/// </summary>
/// <remarks>
/// These settings describe upstream identity providers such as Google or Office 365.
/// They are not OpenIddict client credentials for MCP clients. Values are read from
/// <c>Auth:Providers</c> and overlaid with <c>AppSettings:Auth:Providers</c> so
/// non-secret defaults can live in appsettings while secrets can come from user
/// secrets or production configuration.
/// </remarks>
public sealed record ProviderAuthSettings
{
    /// <summary>
    /// Stable local provider key, for example <c>google</c>.
    /// </summary>
    /// <remarks>
    /// Stored in local claims and ownership rows as audit metadata. It should remain
    /// stable after users have linked identities through this provider.
    /// </remarks>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable provider label shown in the login UI.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client ID assigned to this application by the external provider.
    /// </summary>
    /// <remarks>
    /// Used when starting the authorization-code + PKCE flow against the upstream
    /// provider. This is separate from dynamically registered MCP client IDs and
    /// user-owned client credentials issued by this application.
    /// </remarks>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client secret assigned to this application by the external provider.
    /// </summary>
    /// <remarks>
    /// Used only for the server-to-provider code exchange when the upstream provider
    /// requires a confidential client secret. It should come from secret storage and
    /// should not be logged or sent to MCP clients.
    /// </remarks>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Upstream provider issuer base URI.
    /// </summary>
    /// <remarks>
    /// Used to load the provider discovery document and validate provider-issued ID
    /// tokens. For Google this is typically <c>https://accounts.google.com</c>.
    /// </remarks>
    public string IssuerUri { get; set; } = string.Empty;

    /// <summary>
    /// Public callback URI the provider redirects to after human login.
    /// </summary>
    /// <remarks>
    /// This URI must be registered with the upstream provider. It can differ from
    /// the Vue app origin; the server may use browser handoff after receiving the
    /// callback so cookies are issued on the correct local app origin.
    /// </remarks>
    public string ServerCallbackUri { get; set; } = string.Empty;

    /// <summary>
    /// Scopes requested from the external provider during human login.
    /// </summary>
    /// <remarks>
    /// Defaults are provided by the provider auth catalog when omitted for
    /// known providers. These are provider scopes, not MCP scopes such as
    /// <c>mcp:tools</c>.
    /// </remarks>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// Userinfo endpoint for providers that don't expose OIDC discovery.
    /// </summary>
    /// <remarks>
    /// Used as a fallback when the provider's OIDC discovery document has no
    /// <c>userinfo_endpoint</c>.  GitHub (pure OAuth 2.0) needs this set to
    /// <c>https://api.github.com/user</c>.
    /// </remarks>
    public string UserInfoEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="IssuerUri" /> without a trailing slash for discovery URL composition.
    /// </summary>
    public string IssuerUriTrimmed => IssuerUri.TrimEnd('/');

    /// <summary>
    /// Indicates whether this configured provider has a usable client secret.
    /// </summary>
    public bool HasClientSecret => !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// Indicates whether the provider entry has enough non-placeholder data to be enabled.
    /// </summary>
    /// <remarks>
    /// This intentionally filters common template placeholders so a checked-in sample
    /// provider entry does not appear as enabled in the login UI.
    /// </remarks>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(Name)
        && !ClientSecret.Contains("dotnet user-secrets", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(IssuerUri)
        && !string.IsNullOrWhiteSpace(ServerCallbackUri);
}

/// <summary>
/// Configures how OpenIddict serializes JWT access tokens.
/// </summary>
public enum AccessTokenFormat
{
    /// <summary>
    /// Signed and encrypted JWT access tokens serialized as compact JWE.
    /// </summary>
    EncryptedJwt,

    /// <summary>
    /// Signed-only JWT access tokens serialized as compact JWS.
    /// </summary>
    SignedJwt,
}
