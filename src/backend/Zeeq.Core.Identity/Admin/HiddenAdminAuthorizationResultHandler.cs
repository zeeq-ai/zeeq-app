using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Zeeq.Core.Identity;

/// <summary>
/// Converts unauthorized system-admin route access into 404 while delegating all other routes.
/// </summary>
/// <remarks>
/// The handler is intentionally narrow: it only changes results for matched
/// endpoints carrying <see cref="HiddenAdminRouteMetadata"/>. Non-admin routes,
/// OpenIddict, MCP, and the rest of `/api/v1` keep the framework default
/// challenge/forbid behavior.
/// </remarks>
public sealed class HiddenAdminAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult
    )
    {
        if (!IsHiddenAdminEndpoint(context))
        {
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }

        if (authorizeResult.Succeeded)
        {
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        return Task.CompletedTask;
    }

    private static bool IsHiddenAdminEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<HiddenAdminRouteMetadata>() is not null;
}
