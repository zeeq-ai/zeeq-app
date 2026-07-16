using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Zeeq.Core.Identity;

/// <summary>
/// Ensures an org-scoped route matches the active organization stored in the session cookie.
/// </summary>
/// <remarks>
/// This filter has no store dependency by design. It is the cheap first gate for normalized
/// <c>orgs/{orgId}</c> routes and must run before filters that touch organization state.
/// </remarks>
public sealed class RequireRouteOrganizationMatchesCookieFilter : IEndpointFilter
{
    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var routeOrgId = context.HttpContext.Request.RouteValues["orgId"]?.ToString();
        var cookieOrgId = context.HttpContext.User.AsZeeqMinimalIdentity().OrganizationId;

        if (string.IsNullOrWhiteSpace(routeOrgId))
        {
            return ValueTask.FromResult<object?>(TypedResults.BadRequest());
        }

        if (string.IsNullOrWhiteSpace(cookieOrgId))
        {
            return ValueTask.FromResult<object?>(TypedResults.Unauthorized());
        }

        if (!string.Equals(routeOrgId, cookieOrgId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<object?>(TypedResults.Forbid());
        }

        return next(context);
    }
}
