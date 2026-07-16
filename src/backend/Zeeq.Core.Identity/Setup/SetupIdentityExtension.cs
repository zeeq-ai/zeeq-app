using System.Collections.Immutable;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Configures local authentication for the OpenIddict MCP proof of concept.
/// </summary>
/// <remarks>
/// External IdPs such as the local mock OAuth2 server, Google, or Office 365
/// authenticate the human user. OpenIddict is the local authorization server
/// that issues application cookies, authorization codes, and MCP access tokens
/// from that verified upstream identity.
/// </remarks>
public static class SetupIdentityExtension
{
    /// <summary>
    /// Cookie authentication scheme used by the browser application.
    /// </summary>
    public const string CookieScheme = "zeeq_app_cookie";
    private const string SmartScheme = "zeeq_smart";

    /// <summary>
    /// Registers Zeeq identity, OpenIddict, external IdP login, and authorization services.
    /// </summary>
    public static IServiceCollection AddZeeqIdentity<TContext>(
        this IServiceCollection services,
        AppSettings appSettings,
        IConfiguration configuration,
        IHostEnvironment environment
    )
        where TContext : DbContext
    {
        var authSettings = appSettings.Auth;

        var includeMock = environment.IsDevelopment();

        var providerCatalog = ProviderAuthCatalog.Create(authSettings, includeMock);

        var runtimeSecretsProvider = RuntimeSecretsProviderFactory.Create(
            configuration,
            environment
        );
        runtimeSecretsProvider.ValidateStartup();

        services
            .AddSingleton(appSettings)
            .AddSingleton<
                IAuthorizationMiddlewareResultHandler,
                HiddenAdminAuthorizationResultHandler
            >()
            .AddScoped<IAuthorizationHandler, SystemAdminAuthorizationHandler>()
            .AddScoped<SystemAdminEvaluator>()
            .AddExternalIdpLogin(authSettings, providerCatalog, environment)
            .AddAuthenticationSchemes(authSettings)
            .AddOpenIddictServer<TContext>(authSettings, environment, runtimeSecretsProvider)
            .AddOpenIddictValidation()
            .AddAuthorization();

        return services;
    }

    private static IServiceCollection AddExternalIdpLogin(
        this IServiceCollection services,
        AuthSettings authSettings,
        ProviderAuthCatalog providerCatalog,
        IHostEnvironment environment
    )
    {
        // The stores behind these scoped services use IZeeqAuthStateStore as
        // their boundary. The current in-memory implementation is development-only;
        // production needs DB/Redis-backed one-time state for multi-node safety.
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddHttpClient();
        services.AddSingleton(authSettings);
        services.AddSingleton(providerCatalog);
        services.AddScoped<ExternalLoginStateStore>();
        services.AddScoped<AuthHandoffStore>();
        services.AddScoped<UserTokenGrantTicketStore>();
        services.AddScoped<DcrClientSetupService>();
        services.AddScoped<AuthUserStore>();

        return services;
    }

    private static IServiceCollection AddAuthenticationSchemes(
        this IServiceCollection services,
        AuthSettings authSettings
    )
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = SmartScheme;
                options.DefaultScheme = SmartScheme;
                options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddPolicyScheme(
                SmartScheme,
                "Bearer or app cookie",
                options =>
                {
                    options.ForwardDefaultSelector = context =>
                    {
                        var authorization = context.Request.Headers.Authorization.ToString();

                        return authorization.StartsWith(
                            "Bearer ",
                            StringComparison.OrdinalIgnoreCase
                        )
                            ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                            : CookieScheme;
                    };
                }
            )
            .AddCookie(
                CookieScheme,
                options =>
                {
                    options.Cookie.Name = "zeeq_identity_session";
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.None;
                    options.LoginPath = "/auth/login";
                    options.SlidingExpiration = true;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                }
            )
            .AddMcp(options =>
            {
                options.ResourceMetadataUri = new Uri(
                    authSettings.IssuerTrimmed + "/.well-known/oauth-protected-resource/mcp"
                );
                options.ResourceMetadata = new()
                {
                    Resource = authSettings.ResourceTrimmed,
                    ResourceName = "Zeeq",
                    AuthorizationServers = { authSettings.IssuerTrimmed },
                    BearerMethodsSupported = { "header" },
                    ScopesSupported = { "mcp:tools" },
                };
            });

        return services;
    }

    private static IServiceCollection AddOpenIddictServer<TContext>(
        this IServiceCollection services,
        AuthSettings authSettings,
        IHostEnvironment environment,
        IRuntimeSecretsProvider runtimeSecretsProvider
    )
        where TContext : DbContext
    {
        services
            .AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<TContext>())
            .AddServer(options =>
            {
                options
                    .SetIssuer(new Uri(authSettings.IssuerNormalized))
                    .SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    // MCP clients such as VS Code discover dynamic client registration from the
                    // OAuth authorization server metadata document, not only OIDC discovery.
                    .SetConfigurationEndpointUris(
                        "/.well-known/openid-configuration",
                        "/.well-known/oauth-authorization-server"
                    )
                    .SetJsonWebKeySetEndpointUris("/.well-known/jwks")
                    .AllowAuthorizationCodeFlow()
                    .AllowClientCredentialsFlow()
                    .AllowCustomFlow(UserTokenGrantHandler.GrantType)
                    .AllowRefreshTokenFlow()
                    .SetAuthorizationCodeLifetime(authSettings.AuthorizationCodeLifetime)
                    .SetAccessTokenLifetime(authSettings.InteractiveAccessTokenLifetime)
                    .SetRefreshTokenLifetime(authSettings.RefreshTokenLifetime)
                    .SetRefreshTokenReuseLeeway(authSettings.RefreshTokenReuseLeeway)
                    .DisableSlidingRefreshTokenExpiration()
                    .RequireProofKeyForCodeExchange()
                    .RegisterScopes("openid", "profile", "email", "offline_access", "mcp:tools")
                    .RegisterResources(authSettings.ResourceTrimmed);

                var aspNetCoreOptions = options
                    .UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough();

                if (environment.IsDevelopment())
                {
                    // Local Aspire runs use HTTP through YARP. Production keeps OpenIddict's
                    // default HTTPS requirement so issuer/token endpoints fail closed.
                    aspNetCoreOptions.DisableTransportSecurityRequirement();
                }

                if (authSettings.AccessTokenFormat == AccessTokenFormat.SignedJwt)
                {
                    // This is a server-wide token-format switch. The prototype kept
                    // JWE as the default and used JWS only for compatibility/debugging.
                    options.DisableAccessTokenEncryption();
                }

                runtimeSecretsProvider.ConfigureOpenIddictServer(options);

                options.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(
                    builder =>
                        builder
                            .UseInlineHandler(context =>
                            {
                                // OpenIddict routes are relative for ASP.NET matching behind
                                // Aspire/YARP, but discovery must advertise the public issuer
                                // origin that MCP clients will call.
                                context.AuthorizationEndpoint = new Uri(
                                    authSettings.IssuerTrimmed + "/connect/authorize"
                                );
                                context.TokenEndpoint = new Uri(
                                    authSettings.IssuerTrimmed + "/connect/token"
                                );
                                context.JsonWebKeySetEndpoint = new Uri(
                                    authSettings.IssuerTrimmed + "/.well-known/jwks"
                                );
                                context.Metadata["registration_endpoint"] =
                                    authSettings.IssuerTrimmed + "/connect/register";
                                context.Metadata["token_endpoint_auth_methods_supported"] =
                                    ImmutableArray.Create<string?>(
                                        "none",
                                        "client_secret_basic",
                                        "client_secret_post"
                                    );
                                context.Metadata["grant_types_supported"] =
                                    ImmutableArray.Create<string?>(
                                        GrantTypes.AuthorizationCode,
                                        GrantTypes.ClientCredentials,
                                        GrantTypes.RefreshToken,
                                        UserTokenGrantHandler.GrantType
                                    );
                                context.Metadata["response_types_supported"] =
                                    ImmutableArray.Create<string?>(ResponseTypes.Code);
                                context.Metadata["code_challenge_methods_supported"] =
                                    ImmutableArray.Create<string?>("S256");
                                context.Metadata["scopes_supported"] =
                                    ImmutableArray.Create<string?>(
                                        "openid",
                                        "profile",
                                        "email",
                                        "offline_access",
                                        "mcp:tools"
                                    );
                                context.Metadata["resource_indicators_supported"] =
                                    ImmutableArray.Create<string?>(authSettings.ResourceTrimmed);
                                // TODO(https://github.com/openai/codex/issues/31573): codex-cli's OAuth
                                // callback parser drops the `iss` query parameter it receives but still
                                // enforces strict issuer validation whenever this flag is advertised,
                                // so every codex mcp login fails against an RFC 9207-compliant server.
                                // We still emit `iss` on every response (see ApplyAuthorizationResponseContext
                                // and ApplyTokenResponseContext below); we just don't advertise support here
                                // until the upstream codex bug is fixed. Re-enable once that ships.
                                // context.Metadata["authorization_response_iss_parameter_supported"] = true;
                                // OpenIddict sets this to true internally by default once SetIssuer is
                                // configured, so it must be explicitly overridden here (not just omitted)
                                // to actually suppress the advertisement.
                                context.Metadata["authorization_response_iss_parameter_supported"] =
                                    false;

                                return default;
                            })
                            // OpenIddict's own built-in configuration handler also writes this key;
                            // it must run after ours or it overwrites the override back to true.
                            .SetOrder(int.MaxValue)
                );

                // Include the iss parameter in authorization responses per RFC 9207.
                // MCP clients validate this against the issuer from discovery metadata
                // and fail the OAuth callback if it is missing.
                options.AddEventHandler<OpenIddictServerEvents.ApplyAuthorizationResponseContext>(
                    builder =>
                        builder.UseInlineHandler(context =>
                        {
                            context.Response.Iss = authSettings.IssuerNormalized;
                            return default;
                        })
                );

                // Current MCP clients send RFC 8707 resource parameters. The productionized
                // server rejects missing or mismatched resources instead of rewriting them,
                // because downstream services rely on the token's resource/audience boundary.
                options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(
                    builder =>
                        builder
                            .UseInlineHandler(context =>
                            {
                                var result =
                                    OpenIddictResourceValidator.ValidateAuthorizationRequest(
                                        context.Request.GetResources(),
                                        authSettings.ResourceTrimmed
                                    );
                                if (!result.Succeeded)
                                {
                                    context.Reject(
                                        error: Errors.InvalidRequest,
                                        description: result.Description
                                    );
                                }

                                return default;
                            })
                            .SetOrder(int.MinValue)
                );

                options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(
                    builder =>
                        builder
                            .UseInlineHandler(context =>
                            {
                                var result = OpenIddictResourceValidator.ValidateTokenRequest(
                                    context.Request.GrantType,
                                    context.Request.GetResources(),
                                    authSettings.ResourceTrimmed
                                );
                                if (!result.Succeeded)
                                {
                                    context.Reject(
                                        error: Errors.InvalidRequest,
                                        description: result.Description
                                    );
                                }

                                return default;
                            })
                            .SetOrder(int.MinValue)
                );
                options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(
                    builder =>
                        builder
                            .UseScopedHandler<DcrTokenRequestValidator>()
                            .SetOrder(int.MinValue + 100)
                );

                options.AddEventHandler<OpenIddictServerEvents.HandleTokenRequestContext>(builder =>
                    builder
                        .UseScopedHandler<ClientCredentialsTokenHandler>()
                        .SetOrder(int.MinValue + 100)
                );
                options.AddEventHandler<OpenIddictServerEvents.HandleTokenRequestContext>(builder =>
                    builder.UseScopedHandler<UserTokenGrantHandler>().SetOrder(int.MinValue + 200)
                );

                // Include the iss parameter in token responses per RFC 9207.
                // MCP clients validate this against the issuer from discovery metadata
                // and fail the OAuth callback if it is missing.
                options.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(builder =>
                    builder.UseInlineHandler(context =>
                    {
                        context.Response.Iss = authSettings.IssuerNormalized;
                        return default;
                    })
                );
            });

        return services;
    }

    private static IServiceCollection AddOpenIddictValidation(this IServiceCollection services)
    {
        services
            .AddOpenIddict()
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        return services;
    }

    /// <summary>
    /// Seeds protocol-level OpenIddict scope and MCP application records.
    /// </summary>
    /// <remarks>
    /// This reconciles OpenIddict-owned records that must exist before OAuth/MCP runtime flows
    /// can issue tokens. It intentionally avoids product-owned rows such as users, dynamic client
    /// registration setup metadata, credentials, and user tokens, which are created by the
    /// corresponding runtime workflows.
    /// </remarks>
    public static async Task SeedMcpIdentityAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default
    )
    {
        // Scope and internal-client records live in OpenIddict tables rather than
        // product tables, so startup owns keeping them aligned with AuthSettings.
        using var scope = services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<AuthSettings>();
        var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        var applicationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await scopeManager.FindByNameAsync("mcp:tools", cancellationToken) is null)
        {
            await scopeManager.CreateAsync(
                new OpenIddictScopeDescriptor
                {
                    Name = "mcp:tools",
                    DisplayName = "MCP Tools Access",
                    Resources = { settings.ResourceTrimmed },
                },
                cancellationToken
            );
        }

        var internalUserTokenClient = await applicationManager.FindByClientIdAsync(
            settings.InternalUserTokenClientId,
            cancellationToken
        );
        var internalUserTokenClientDescriptor =
            UserTokenInternalClientFactory.CreateApplicationDescriptor(settings);

        if (internalUserTokenClient is null)
        {
            await applicationManager.CreateAsync(
                internalUserTokenClientDescriptor,
                cancellationToken
            );
        }
        else
        {
            await applicationManager.UpdateAsync(
                internalUserTokenClient,
                internalUserTokenClientDescriptor,
                cancellationToken
            );
        }
    }
}
