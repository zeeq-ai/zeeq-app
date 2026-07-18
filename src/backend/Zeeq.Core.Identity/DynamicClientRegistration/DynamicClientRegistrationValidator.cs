namespace Zeeq.Core.Identity;

/// <summary>
/// Validates the local public/native subset of Dynamic Client Registration.
/// </summary>
/// <remarks>
/// The sandbox prototype proved this subset is enough for local MCP clients:
/// public native clients, authorization code + PKCE, refresh tokens, loopback
/// HTTP/HTTPS redirects, the Cursor private-scheme callback, and the MCP tools
/// scope/resource boundary.
/// </remarks>
public static class DynamicClientRegistrationValidator
{
    private static readonly string[] DefaultScopes = ["mcp:tools", "openid", "profile", "email"];
    private static readonly HashSet<string> AllowedScopes =
    [
        "mcp:tools",
        "openid",
        "profile",
        "email",
        "offline_access",
    ];

    /// <summary>
    /// Returns an OAuth error when unsupported client metadata is requested.
    /// </summary>
    public static OAuthError? Validate(
        DynamicClientRegistrationRequest request,
        DynamicClientRegistrationSettings settings
    )
    {
        if (request.RedirectUris is null || request.RedirectUris.Count == 0)
        {
            return InvalidClientMetadata("At least one redirect_uri is required.");
        }

        // Claude Desktop/claude.ai register through the hosted claude.ai callback rather
        // than a loopback redirect, and its registration request reflects that hosted-web
        // shape: token_endpoint_auth_method "client_secret_post" and application_type "web".
        // We still always create a public/native OpenIddict application (see
        // DynamicClientRegistrationEndpoints) and require PKCE regardless, so honoring
        // those requested values is just not rejecting a legitimate client shape - it does
        // not loosen validation for the loopback/native clients this PoC was built around.
        var isClaudeHostedClient = IsClaudeHostedRedirectUris(request.RedirectUris, settings);

        if (
            !isClaudeHostedClient
            && request.TokenEndpointAuthMethod is not null
            && request.TokenEndpointAuthMethod != "none"
        )
        {
            return InvalidClientMetadata(
                "Only token_endpoint_auth_method 'none' is supported by this PoC."
            );
        }

        if (
            !isClaudeHostedClient
            && request.ApplicationType is not null
            && request.ApplicationType != "native"
        )
        {
            return InvalidClientMetadata(
                "Only application_type 'native' is supported by this PoC."
            );
        }

        foreach (var redirectUri in request.RedirectUris)
        {
            if (!IsAllowedRedirectUri(redirectUri, settings))
            {
                return InvalidRedirectUri(
                    $"Redirect URI '{redirectUri}' must be loopback HTTP, HTTPS, or Cursor's private callback URI and cannot include a fragment."
                );
            }
        }

        foreach (var grantType in request.GrantTypes ?? ["authorization_code"])
        {
            if (grantType is not "authorization_code" and not "refresh_token")
            {
                return InvalidClientMetadata($"Grant type '{grantType}' is not supported.");
            }
        }

        foreach (var responseType in request.ResponseTypes ?? ["code"])
        {
            if (responseType != "code")
            {
                return InvalidClientMetadata($"Response type '{responseType}' is not supported.");
            }
        }

        foreach (var scope in NormalizeScopes(request.Scope))
        {
            if (!AllowedScopes.Contains(scope))
            {
                return InvalidClientMetadata($"Scope '{scope}' is not supported.");
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes requested scopes and guarantees <c>mcp:tools</c> is present.
    /// </summary>
    /// <remarks>
    /// MCP clients may omit the scope during DCR, but downstream authorization
    /// and token requests depend on the tools scope being registered on the
    /// OpenIddict application.
    /// </remarks>
    public static IReadOnlyList<string> NormalizeScopes(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return DefaultScopes;
        }

        var requested = scope.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return requested.Contains("mcp:tools") ? requested : [.. requested, "mcp:tools"];
    }

    private static bool IsAllowedRedirectUri(
        string value,
        DynamicClientRegistrationSettings settings
    )
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // Native public clients use loopback callbacks for local agents and inspectors.
        // HTTPS remains allowed for clients that host their own callback endpoint. Known
        // native clients such as Cursor use a fixed private-scheme callback instead.
        return uri.Scheme == Uri.UriSchemeHttps
            || IsAllowedStaticRedirectUri(uri, settings)
            || (
                uri.Scheme == Uri.UriSchemeHttp
                && settings.AllowedLoopbackHosts.Contains(uri.Host, StringComparer.Ordinal)
            );
    }

    /// <summary>
    /// Whether every requested redirect URI is one of the known claude.ai hosted callbacks.
    /// </summary>
    /// <remarks>
    /// Used to scope the token_endpoint_auth_method/application_type exception to Claude
    /// specifically rather than relaxing it for every HTTPS-callback client.
    /// </remarks>
    private static bool IsClaudeHostedRedirectUris(
        IReadOnlyList<string> redirectUris,
        DynamicClientRegistrationSettings settings
    ) =>
        redirectUris.All(value =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Host == "claude.ai"
            && IsAllowedStaticRedirectUri(uri, settings)
        );

    private static bool IsAllowedStaticRedirectUri(
        Uri uri,
        DynamicClientRegistrationSettings settings
    ) =>
        settings.AllowedStaticRedirectUris.Any(allowed =>
            Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri)
            && allowedUri.Scheme == uri.Scheme
            && allowedUri.Host == uri.Host
            && allowedUri.AbsolutePath == uri.AbsolutePath
        );

    private static OAuthError InvalidClientMetadata(string description) =>
        new("invalid_client_metadata", description);

    private static OAuthError InvalidRedirectUri(string description) =>
        new("invalid_redirect_uri", description);
}
