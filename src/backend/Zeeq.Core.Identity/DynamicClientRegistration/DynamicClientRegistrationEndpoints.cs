using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Dynamic Client Registration endpoint used by local MCP clients.
/// </summary>
/// <remarks>
/// This is intentionally a small RFC 7591-compatible subset for the PoC. It
/// stores clients in OpenIddict's application table and defaults to public
/// native clients using authorization code + PKCE.
/// </remarks>
public static class DynamicClientRegistrationEndpoints
{
    private const string PublicTokenEndpointAuthMethod = "none";

    /// <summary>
    /// Maps the anonymous DCR endpoint that creates public/native MCP OAuth clients.
    /// </summary>
    /// <remarks>
    /// Registration writes both the OpenIddict application and a local pending setup row.
    /// If local setup persistence fails, the OpenIddict application is deleted as
    /// compensation so an orphan public client is not left behind.
    /// </remarks>
    public static IEndpointRouteBuilder MapDynamicClientRegistration(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/connect/register",
                async Task<
                    Results<
                        JsonHttpResult<DynamicClientRegistrationResponse>,
                        BadRequest<OAuthError>
                    >
                > (
                    DynamicClientRegistrationRequest request,
                    IOpenIddictApplicationManager manager,
                    DcrClientSetupService setupService,
                    AuthSettings settings,
                    ILoggerFactory loggerFactory,
                    CancellationToken cancellationToken
                ) =>
                {
                    var validation = DynamicClientRegistrationValidator.Validate(
                        request,
                        settings.DynamicClientRegistration
                    );

                    if (validation is not null)
                    {
                        // Logging for the DCR request failure
                        var logger = loggerFactory.CreateLogger("DynamicClientRegistration");

                        logger.LogWarning(
                            "Received failed DCR request: {Request}",
                            JsonSerializer.Serialize(request)
                        );

                        logger.LogWarning(
                            "DCR request rejected: {Error} - {Description}",
                            validation.Error,
                            validation.ErrorDescription
                        );

                        return TypedResults.BadRequest(validation);
                    }

                    var clientId = $"mcp_{Guid.NewGuid():N}";
                    var scopes = DynamicClientRegistrationValidator.NormalizeScopes(request.Scope);

                    var descriptor = new OpenIddictApplicationDescriptor
                    {
                        ApplicationType = ApplicationTypes.Native,
                        ClientId = clientId,
                        ClientType = ClientTypes.Public,
                        ConsentType = ConsentTypes.Implicit,
                        DisplayName = string.IsNullOrWhiteSpace(request.ClientName)
                            ? "MCP Client"
                            : request.ClientName,
                        Permissions =
                        {
                            Permissions.Endpoints.Authorization,
                            Permissions.Endpoints.Token,
                            Permissions.GrantTypes.AuthorizationCode,
                            Permissions.GrantTypes.RefreshToken,
                            Permissions.ResponseTypes.Code,
                            Permissions.Prefixes.Resource + settings.ResourceTrimmed,
                        },
                        Requirements = { Requirements.Features.ProofKeyForCodeExchange },
                    };

                    foreach (var redirectUri in request.RedirectUris!)
                    {
                        descriptor.RedirectUris.Add(new Uri(redirectUri));
                    }

                    foreach (var scope in scopes)
                    {
                        descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
                    }

                    await manager.CreateAsync(descriptor, cancellationToken);

                    try
                    {
                        // The client exists in OpenIddict now, but it is not usable
                        // until the first authorization request binds it to a logged-in
                        // local user through DcrClientSetupService.
                        await setupService.CreatePendingAsync(
                            clientId,
                            descriptor.DisplayName,
                            request.RedirectUris!,
                            scopes,
                            cancellationToken
                        );
                    }
                    catch
                    {
                        // OpenIddict and local setup rows are separate stores. Keep
                        // registration transactional from the caller's perspective by
                        // deleting the application if local setup persistence fails.
                        var application = await manager.FindByClientIdAsync(
                            clientId,
                            cancellationToken
                        );
                        if (application is not null)
                        {
                            await manager.DeleteAsync(application, cancellationToken);
                        }

                        throw;
                    }

                    return TypedResults.Json(
                        new DynamicClientRegistrationResponse(
                            ClientId: clientId,
                            ClientIdIssuedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ClientName: descriptor.DisplayName,
                            RedirectUris: request.RedirectUris!,
                            GrantTypes: ["authorization_code", "refresh_token"],
                            ResponseTypes: ["code"],
                            Scope: string.Join(' ', scopes),
                            TokenEndpointAuthMethod: PublicTokenEndpointAuthMethod,
                            ApplicationType: "native"
                        ),
                        statusCode: StatusCodes.Status201Created
                    );
                }
            )
            .AllowAnonymous();

        return app;
    }
}
