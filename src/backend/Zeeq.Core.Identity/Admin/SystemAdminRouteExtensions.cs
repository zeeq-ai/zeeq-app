using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Maps the shared system-admin API route group.
/// </summary>
public static class SystemAdminRouteExtensions
{
    /// <summary>
    /// Creates the `/admin` subgroup under the supplied API root with live admin authorization.
    /// </summary>
    public static RouteGroupBuilder MapSystemAdminGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup("/admin")
            .WithMetadata(new HiddenAdminRouteMetadata())
            .RequireAuthorization(policy => policy.AddRequirements(new SystemAdminRequirement()));
}
