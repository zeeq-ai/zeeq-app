using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Core.Identity;

/// <summary>
/// Succeeds the system-admin requirement when the current principal matches live configuration.
/// </summary>
/// <remarks>
/// ASP.NET Core authorization invokes this handler after routing selects an
/// endpoint whose metadata includes <see cref="SystemAdminRequirement" />. In
/// the admin route flow, that metadata comes from
/// <see cref="SystemAdminRouteExtensions.MapSystemAdminGroup" /> and applies to
/// every child route under <c>/api/v1/admin</c>.
///
/// This handler owns the final allow/deny decision for the requirement: it
/// requires an authenticated principal and delegates the admin-subject check to
/// <see cref="SystemAdminEvaluator" /> so grants and revocations are evaluated
/// against current configuration on each request.
/// </remarks>
public sealed class SystemAdminAuthorizationHandler(SystemAdminEvaluator systemAdminEvaluator)
    : AuthorizationHandler<SystemAdminRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SystemAdminRequirement requirement
    )
    {
        if (
            context.User.Identity?.IsAuthenticated == true
            && systemAdminEvaluator.IsSystemAdmin(context.User)
        )
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
