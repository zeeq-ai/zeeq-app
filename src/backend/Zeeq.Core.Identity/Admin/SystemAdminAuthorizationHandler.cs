using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Core.Identity;

/// <summary>
/// Succeeds the system-admin requirement when the current principal matches live configuration.
/// </summary>
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
