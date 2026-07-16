using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity;

/// <summary>
/// External IdP login endpoints for the local mock provider and Google.
/// </summary>
/// <remarks>
/// External providers authenticate humans; this application converts the verified
/// provider identity into local cookie claims and later OpenIddict artifacts.
/// Browser-origin logins may require a one-time handoff so the session cookie is
/// issued on the same origin the web app will use for <c>/me</c> and auth routes.
/// </remarks>
public static class ExternalLoginEndpoints
{
    private const string ActivationPath = "/activate-organization";
    private const string RetiredActivationPath = "/activate-account";

    /// <summary>
    /// Maps provider discovery, login start, callback, handoff, and logout routes.
    /// </summary>
    public static IEndpointRouteBuilder MapExternalLoginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/providers", GetProviders).ExcludeFromDescription().AllowAnonymous();

        app.MapGet("/auth/login", ShowProviderPicker).ExcludeFromDescription().AllowAnonymous();

        app.MapGet("/auth/login/{provider}", StartProviderLoginAsync)
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapGet("/auth/callback/{provider}", CompleteProviderLoginAsync)
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapGet("/auth/complete/{provider}", CompleteBrowserHandoffAsync)
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapPost("/auth/logout", (Func<HttpContext, Task<IResult>>)LogoutAsync)
            .ExcludeFromDescription()
            .AllowAnonymous();

        return app;
    }

    private static Ok<IReadOnlyList<ProviderSummary>> GetProviders(
        ProviderAuthCatalog providerCatalog
    )
    {
        var providers = providerCatalog
            .Providers.OrderBy(provider => provider.Name == "mock" ? 0 : 1)
            .Select(provider => new ProviderSummary(
                Name: provider.Name,
                DisplayName: provider.DisplayName,
                Enabled: provider.Name == "mock" || provider.IsConfigured,
                LoginUrl: $"/auth/login/{provider.Name}"
            ))
            .ToArray();

        return TypedResults.Ok<IReadOnlyList<ProviderSummary>>(providers);
    }

    private static ContentHttpResult ShowProviderPicker(
        string? returnUrl,
        AuthSettings settings,
        ProviderAuthCatalog providerCatalog
    )
    {
        var safeReturnUrl = NormalizeReturnUrl(returnUrl, settings);
        var links = providerCatalog
            .Providers.OrderBy(provider => provider.Name == "mock" ? 0 : 1)
            .Select(provider =>
            {
                var href =
                    $"/auth/login/{Uri.EscapeDataString(provider.Name)}?returnUrl={Uri.EscapeDataString(safeReturnUrl)}";
                return $"""
                <li><a href="{WebUtility.HtmlEncode(href)}">{WebUtility.HtmlEncode(
                    provider.DisplayName
                )}</a></li>
                """;
            });

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>Choose sign-in provider</title>
              <style>
                body { font-family: system-ui, sans-serif; margin: 3rem; color: #17202a; }
                a { color: #2355a6; font-weight: 650; }
              </style>
            </head>
            <body>
              <h1>Choose sign-in provider</h1>
              <ul>
                {{string.Join(Environment.NewLine, links)}}
              </ul>
            </body>
            </html>
            """;

        return TypedResults.Content(html, "text/html");
    }

    private static async Task<
        Results<RedirectHttpResult, BadRequest<OAuthError>, NotFound<OAuthError>>
    > StartProviderLoginAsync(
        string provider,
        string? returnUrl,
        ProviderAuthCatalog providerCatalog,
        AuthSettings settings,
        ExternalLoginStateStore stateStore,
        HttpContext httpContext,
        CancellationToken cancellationToken
    )
    {
        var authProvider = providerCatalog.Find(provider);
        if (authProvider is null)
        {
            return TypedResults.NotFound(
                new OAuthError("invalid_request", $"Provider '{provider}' is not configured.")
            );
        }

        if (authProvider.Name != "mock" && !authProvider.IsConfigured)
        {
            return TypedResults.BadRequest(
                new OAuthError("invalid_request", $"Provider '{provider}' is not enabled.")
            );
        }

        var codeVerifier = Pkce.CreateCodeVerifier();
        var codeChallenge = Pkce.CreateS256Challenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");
        var safeReturnUrl = NormalizeReturnUrl(returnUrl, settings);
        var redirectUri =
            authProvider.Name == "mock"
                ? GetMockCallbackUri(httpContext, settings, safeReturnUrl)
                : authProvider.ServerCallbackUri;

        // Store the normalized return URL before leaving our origin. The callback
        // must not trust a fresh returnUrl parameter after provider authentication.
        await stateStore.StoreAsync(
            state,
            new OAuthState(
                ProviderName: authProvider.Name,
                CodeVerifier: codeVerifier,
                RedirectUri: redirectUri,
                ReturnUrl: safeReturnUrl,
                ExpiresAt: DateTimeOffset.UtcNow.Add(settings.LoginStateLifetime)
            ),
            cancellationToken
        );

        var configuration = await ReadProviderConfigurationAsync(authProvider, cancellationToken);
        var authorizeUrl = BuildUrl(
            configuration.AuthorizationEndpoint,
            new Dictionary<string, string>
            {
                ["client_id"] = authProvider.ClientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = string.Join(' ', authProvider.Scopes),
                ["state"] = state,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
            }
        );

        return TypedResults.Redirect(authorizeUrl);
    }

    private static async Task<IResult> CompleteProviderLoginAsync(
        string? code,
        string? state,
        string? error,
        string? error_description,
        ProviderAuthCatalog providerCatalog,
        AuthSettings settings,
        ExternalLoginStateStore stateStore,
        AuthHandoffStore handoffStore,
        AuthUserStore userStore,
        AppSettings appSettings,
        IHttpClientFactory httpClientFactory,
        HttpContext httpContext,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Results.Redirect(
                $"{settings.FrontendBaseUriTrimmed}/login?error={Uri.EscapeDataString(error_description ?? error)}"
            );
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return Results.Redirect(
                $"{settings.FrontendBaseUriTrimmed}/login?error={Uri.EscapeDataString("The code and state parameters are required.")}"
            );
        }

        var oauthState = await stateStore.ConsumeAsync(state, cancellationToken);
        if (oauthState is null)
        {
            return Results.Redirect(
                $"{settings.FrontendBaseUriTrimmed}/login?error={Uri.EscapeDataString("The state parameter is invalid or expired.")}"
            );
        }

        var provider = providerCatalog.Find(oauthState.ProviderName);
        if (provider is null)
        {
            return Results.Redirect(
                $"{settings.FrontendBaseUriTrimmed}/login?error={Uri.EscapeDataString("The state references an unknown provider.")}"
            );
        }

        var http = httpClientFactory.CreateClient();
        var tokenResponse = await ExchangeCodeAsync(
            http,
            provider,
            code,
            oauthState,
            cancellationToken
        );
        var principal = await CreatePrincipalAsync(
            http,
            provider,
            tokenResponse,
            userStore,
            appSettings,
            cancellationToken
        );

        if (ShouldUseBrowserHandoff(httpContext, settings, oauthState.ReturnUrl))
        {
            // Google and other real providers may callback to the API host, while
            // the browser app needs a host-scoped cookie on the frontend origin.
            // The short-lived ticket bridges that origin boundary without putting
            // the principal in the URL.
            var ticket = await handoffStore.StoreAsync(
                new AuthHandoff(
                    Principal: principal,
                    ReturnUrl: oauthState.ReturnUrl,
                    ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(2)
                ),
                cancellationToken
            );

            var handoffUrl = new Uri(
                new Uri(settings.FrontendBaseUriTrimmed + "/"),
                $"auth/complete/{Uri.EscapeDataString(oauthState.ProviderName)}?ticket={Uri.EscapeDataString(ticket)}"
            );

            return Results.Redirect(handoffUrl.ToString());
        }

        await httpContext.SignInAsync(SetupIdentityExtension.CookieScheme, principal);

        return Results.Redirect(NormalizeReturnUrl(oauthState.ReturnUrl, settings));
    }

    private static async Task<IResult> CompleteBrowserHandoffAsync(
        string? ticket,
        AuthHandoffStore handoffStore,
        AuthSettings settings,
        HttpContext httpContext,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return Results.BadRequest(
                new OAuthError("invalid_request", "The handoff ticket is required.")
            );
        }

        var handoff = await handoffStore.ConsumeAsync(ticket, cancellationToken);
        if (handoff is null)
        {
            return Results.BadRequest(
                new OAuthError("invalid_request", "The handoff ticket is invalid or expired.")
            );
        }

        await httpContext.SignInAsync(SetupIdentityExtension.CookieScheme, handoff.Principal);

        return Results.Redirect(NormalizeReturnUrl(handoff.ReturnUrl, settings));
    }

    private static async Task<IResult> LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(SetupIdentityExtension.CookieScheme);

        return Results.NoContent();
    }

    private static async Task<ProviderTokenResponse> ExchangeCodeAsync(
        HttpClient http,
        ProviderAuthSettings provider,
        string code,
        OAuthState state,
        CancellationToken cancellationToken
    )
    {
        var configuration = await ReadProviderConfigurationAsync(provider, cancellationToken);
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = state.RedirectUri,
            ["client_id"] = provider.ClientId,
            ["code_verifier"] = state.CodeVerifier,
        };

        if (provider.HasClientSecret)
        {
            // Providers such as Google use a confidential server-side exchange.
            // Public local mock flows can omit the secret.
            body["client_secret"] = provider.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(body),
        };
        request.Headers.Accept.ParseAdd("application/json");

        var response = await http.SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ProviderTokenResponse>(
                cancellationToken: cancellationToken
            ) ?? throw new InvalidOperationException("Provider returned an empty token response.");
    }

    private static async Task<ClaimsPrincipal> CreatePrincipalAsync(
        HttpClient http,
        ProviderAuthSettings provider,
        ProviderTokenResponse tokenResponse,
        AuthUserStore userStore,
        AppSettings appSettings,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(tokenResponse.IdToken))
        {
            // Prefer the signed ID token as identity proof, then use userinfo only
            // to fill profile fields that are absent from the token.
            var principal = await ValidateIdTokenAsync(
                provider,
                tokenResponse.IdToken,
                cancellationToken
            );
            var userInfo = !string.IsNullOrWhiteSpace(tokenResponse.AccessToken)
                ? await ReadUserInfoAsync(
                    http,
                    provider,
                    tokenResponse.AccessToken,
                    cancellationToken
                )
                : null;

            var providerSubject =
                principal.FindFirstValue(OpenIddictConstants.Claims.Subject)
                ?? throw new InvalidOperationException("Provider id_token has no sub claim.");
            var name = principal.FindFirstValue(OpenIddictConstants.Claims.Name) ?? userInfo?.Name;
            var email =
                principal.FindFirstValue(OpenIddictConstants.Claims.Email) ?? userInfo?.Email;
            var pictureUrl =
                principal.FindFirstValue(OpenIddictConstants.Claims.Picture)
                ?? userInfo?.PictureUrl;
            var authContext = await userStore.EnsureUserAsync(
                provider.Name,
                providerSubject,
                name,
                email,
                pictureUrl,
                cancellationToken
            );

            return ExternalUserPrincipalFactory.CreateCookiePrincipal(
                authContext,
                provider.Name,
                providerSubject,
                name,
                email,
                pictureUrl,
                orgSlug: null,
                isSystemAdmin: SystemAdminSubjectMatcher.IsSystemAdminSubject(
                    provider.Name,
                    providerSubject,
                    appSettings.Platform.SystemAdminSubjects
                )
            );
        }

        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            throw new InvalidOperationException("Provider returned no id_token or access_token.");
        }

        // Some providers return no ID token for this style of flow. In that case,
        // userinfo becomes the identity source and must include a stable subject.
        var fallbackUserInfo =
            await ReadUserInfoAsync(http, provider, tokenResponse.AccessToken, cancellationToken)
            ?? throw new InvalidOperationException("Provider returned an empty userinfo response.");

        var fallbackProviderSubject =
            fallbackUserInfo.EffectiveSubject
            ?? throw new InvalidOperationException("Provider userinfo has no sub claim.");
        var fallbackAuthContext = await userStore.EnsureUserAsync(
            provider.Name,
            fallbackProviderSubject,
            fallbackUserInfo.Name,
            fallbackUserInfo.Email,
            fallbackUserInfo.PictureUrl,
            cancellationToken
        );

        return ExternalUserPrincipalFactory.CreateCookiePrincipal(
            fallbackAuthContext,
            provider.Name,
            fallbackProviderSubject,
            fallbackUserInfo.Name,
            fallbackUserInfo.Email,
            fallbackUserInfo.PictureUrl,
            orgSlug: null,
            isSystemAdmin: SystemAdminSubjectMatcher.IsSystemAdminSubject(
                provider.Name,
                fallbackProviderSubject,
                appSettings.Platform.SystemAdminSubjects
            )
        );
    }

    private static async Task<ProviderUserInfo?> ReadUserInfoAsync(
        HttpClient http,
        ProviderAuthSettings provider,
        string accessToken,
        CancellationToken cancellationToken
    )
    {
        var configuration = await ReadProviderConfigurationAsync(provider, cancellationToken);
        var userInfoEndpoint = configuration.UserInfoEndpoint ?? provider.UserInfoEndpoint;
        if (string.IsNullOrWhiteSpace(userInfoEndpoint))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
        request.Headers.Authorization = new("Bearer", accessToken);
        // GitHub (and other providers) require a User-Agent header on API requests
        // or they return 403 Forbidden.  Without this, ReadFromJsonAsync never runs.
        request.Headers.UserAgent.ParseAdd("Zeeq/1.0");

        var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ProviderUserInfo>(
            cancellationToken: cancellationToken
        );
    }

    private static string GetMockCallbackUri(
        HttpContext httpContext,
        AuthSettings settings,
        string returnUrl
    )
    {
        // The local mock provider can callback to the Vue origin, which avoids the
        // Google-style handoff and keeps the cookie same-origin for browser tests.
        if (Uri.TryCreate(settings.FrontendBaseUri, UriKind.Absolute, out var frontendUri))
        {
            var forwardedHost = httpContext
                .Request.Headers["X-Forwarded-Host"]
                .ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (
                HostMatches(httpContext.Request.Host.ToString(), frontendUri)
                || HostMatches(forwardedHost, frontendUri)
                || ReturnUrlMatches(returnUrl, frontendUri)
            )
            {
                return new Uri(frontendUri, "/auth/callback/mock").ToString();
            }
        }

        return settings.MockCallbackUri;
    }

    private static string NormalizeReturnUrl(string? returnUrl, AuthSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return BuildFrontendReturnUrl("/", settings);
        }

        if (IsActivationReturnUrl(returnUrl, settings))
        {
            return BuildFrontendReturnUrl("/", settings);
        }

        if (IsLocalReturnUrl(returnUrl))
        {
            return BuildFrontendReturnUrl(returnUrl, settings);
        }

        if (settings is not null && IsTrustedAbsoluteReturnUrl(returnUrl, settings))
        {
            return returnUrl;
        }

        // Fail closed to the app root for off-site, scheme-relative, malformed,
        // or otherwise untrusted return targets.
        return BuildFrontendReturnUrl("/", settings);
    }

    private static string BuildFrontendReturnUrl(string localReturnUrl, AuthSettings? settings)
    {
        var normalizedLocalPath = localReturnUrl.StartsWith("~/", StringComparison.Ordinal)
            ? localReturnUrl[1..]
            : localReturnUrl;

        if (
            settings is null
            || !Uri.TryCreate(
                settings.FrontendBaseUriTrimmed + "/",
                UriKind.Absolute,
                out var baseUri
            )
        )
        {
            return normalizedLocalPath;
        }

        var relativePath =
            normalizedLocalPath.Length == 1 ? string.Empty : normalizedLocalPath.TrimStart('/');

        return new Uri(baseUri, relativePath).ToString();
    }

    /// <summary>
    /// Blocks activation URLs from being replayed as OAuth return targets.
    /// </summary>
    private static bool IsActivationReturnUrl(string returnUrl, AuthSettings? settings)
    {
        if (IsLocalReturnUrl(returnUrl))
        {
            var localPath = returnUrl.StartsWith("~/", StringComparison.Ordinal)
                ? returnUrl[1..]
                : returnUrl;

            return IsActivationTarget(localPath);
        }

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (
            settings is null
            || !Uri.TryCreate(
                settings.FrontendBaseUriTrimmed + "/",
                UriKind.Absolute,
                out var frontendUri
            )
        )
        {
            return IsActivationTarget(candidate.PathAndQuery);
        }

        var localCandidatePath = candidate.AbsolutePath;
        var basePath = frontendUri.AbsolutePath.TrimEnd('/');
        if (
            basePath.Length > 0
            && localCandidatePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)
        )
        {
            localCandidatePath = localCandidatePath[basePath.Length..];
        }
        var localCandidate = localCandidatePath + candidate.Query;

        return IsActivationTarget(localCandidate);
    }

    private static bool IsActivationTarget(string target)
    {
        if (!Uri.TryCreate("http://localhost" + target, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return IsActivationPath(parsed.AbsolutePath)
            || (
                string.Equals(parsed.AbsolutePath, "/login", StringComparison.OrdinalIgnoreCase)
                && QueryContainsInactiveOrg(parsed.Query)
            );
    }

    private static bool QueryContainsInactiveOrg(string query) =>
        query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(parameter =>
                string.Equals(parameter, "inactiveOrg=true", StringComparison.OrdinalIgnoreCase)
            );

    private static bool IsActivationPath(string path) =>
        string.Equals(path, ActivationPath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(ActivationPath + "/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, RetiredActivationPath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(RetiredActivationPath + "/", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUseBrowserHandoff(
        HttpContext httpContext,
        AuthSettings settings,
        string returnUrl
    )
    {
        if (!Uri.TryCreate(settings.FrontendBaseUri, UriKind.Absolute, out var frontendUri))
        {
            return false;
        }

        // Relative return URLs (e.g. "/") will resolve to whatever host the
        // callback lands on (e.g. localhost:8096), not the frontend origin.
        // Always use the browser handoff in that case.
        if (IsLocalReturnUrl(returnUrl))
        {
            return !HostMatches(httpContext.Request.Host.ToString(), frontendUri);
        }

        // Absolute return URLs: only use handoff when the return URL targets
        // the frontend origin but the current request host does not.
        return ReturnUrlMatches(returnUrl, frontendUri)
            && !HostMatches(httpContext.Request.Host.ToString(), frontendUri);
    }

    private static async Task<ClaimsPrincipal> ValidateIdTokenAsync(
        ProviderAuthSettings provider,
        string idToken,
        CancellationToken cancellationToken
    )
    {
        var configuration = await ReadProviderConfigurationAsync(provider, cancellationToken);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        return handler.ValidateToken(
            idToken,
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = configuration.Issuer,
                ValidateAudience = true,
                ValidAudience = provider.ClientId,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                NameClaimType = OpenIddictConstants.Claims.Name,
            },
            out _
        );
    }

    private static async Task<OpenIdConnectConfiguration> ReadProviderConfigurationAsync(
        ProviderAuthSettings provider,
        CancellationToken cancellationToken
    )
    {
        var configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            provider.IssuerUriTrimmed + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever
            {
                RequireHttps = provider.IssuerUriTrimmed.StartsWith("https://"),
            }
        );

        return await configurationManager.GetConfigurationAsync(cancellationToken);
    }

    private static string BuildUrl(string baseUrl, IReadOnlyDictionary<string, string> values) =>
        baseUrl
        + "?"
        + string.Join(
            "&",
            values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")
        );

    private static bool HostMatches(string? host, Uri expected)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return string.Equals(
            HostString.FromUriComponent(host).Host,
            expected.Host,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static bool ReturnUrlMatches(string returnUrl, Uri expected) =>
        Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalReturnUrl(string returnUrl)
    {
        if (returnUrl[0] == '/')
        {
            return returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\');
        }

        return returnUrl.Length > 1
            && returnUrl[0] == '~'
            && returnUrl[1] == '/'
            && (returnUrl.Length == 2 || (returnUrl[2] != '/' && returnUrl[2] != '\\'));
    }

    private static bool IsTrustedAbsoluteReturnUrl(string returnUrl, AuthSettings settings)
    {
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        return IsSameOrigin(candidate, settings.FrontendBaseUri)
            || IsSameOrigin(candidate, settings.IssuerNormalized);
    }

    private static bool IsSameOrigin(Uri candidate, string trustedOrigin) =>
        Uri.TryCreate(trustedOrigin, UriKind.Absolute, out var trusted)
        && string.Equals(candidate.Scheme, trusted.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.Host, trusted.Host, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == trusted.Port;
}
