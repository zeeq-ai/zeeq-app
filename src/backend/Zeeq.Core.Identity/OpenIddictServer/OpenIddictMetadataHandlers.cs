using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Publishes OAuth protected resource metadata for MCP clients.
/// </summary>
/// <remarks>
/// MCP clients discover the authorization server from the resource challenge
/// before they know the OpenIddict issuer. Keeping this explicit route avoids
/// relying on SDK defaults and documents the exact resource/scope boundary.
/// </remarks>
public static class OpenIddictMetadataHandlers
{
    private static readonly string[] data = new[] { "header" };
    private static readonly string[] dataArray = new[] { "mcp:tools" };

    /// <summary>
    /// Maps protected-resource metadata at the generic and MCP-specific well-known paths.
    /// </summary>
    public static IEndpointRouteBuilder MapMcpProtectedResourceMetadata(
        this IEndpointRouteBuilder app
    )
    {
        app.MapGet("/.well-known/oauth-protected-resource", WriteProtectedResourceMetadata)
            .ExcludeFromDescription()
            .AllowAnonymous();

        app.MapGet("/.well-known/oauth-protected-resource/mcp", WriteProtectedResourceMetadata)
            .ExcludeFromDescription()
            .AllowAnonymous();

        return app;

        static IResult WriteProtectedResourceMetadata(AuthSettings settings) =>
            Results.Json(
                new Dictionary<string, object?>
                {
                    ["resource"] = settings.ResourceTrimmed,
                    ["authorization_servers"] = new[] { settings.IssuerNormalized },
                    ["scopes_supported"] = dataArray,
                    ["bearer_methods_supported"] = data,
                    ["resource_name"] = "Zeeq",
                }
            );
    }
}
