using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Groups identity route mapping by auth surface.
/// </summary>
/// <remarks>
/// Browser auth routes and OAuth/MCP routes are kept separate so callers can
/// choose the appropriate surface for Aspire/YARP routing. Browser routes use
/// cookies; OAuth/MCP routes produce discovery metadata, DCR clients, codes, and tokens.
/// </remarks>
public static class SetupAuthRoutesExtension
{
    /// <summary>
    /// Maps every identity route group required by the API.
    /// </summary>
    public static IEndpointRouteBuilder MapZeeqIdentityRoutes(this IEndpointRouteBuilder app)
    {
        app.MapAuthRoutes();
        app.MapOAuthRoutes();

        return app;
    }

    /// <summary>
    /// Maps browser-facing external IdP login routes.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthRoutes(this IEndpointRouteBuilder app)
    {
        app.MapExternalLoginEndpoints();

        return app;
    }

    /// <summary>
    /// Maps OAuth/OIDC and MCP discovery routes.
    /// </summary>
    public static IEndpointRouteBuilder MapOAuthRoutes(this IEndpointRouteBuilder app)
    {
        app.MapMcpProtectedResourceMetadata();
        app.MapDynamicClientRegistration();
        app.MapAuthorizationEndpoints();

        return app;
    }
}
