using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Maps the shared system-admin API route group.
/// </summary>
/// <remarks>
/// System-admin security is applied by route-group mapping, not by individual
/// endpoint handlers.
///
/// Mechanism of action:
/// <list type="number">
///   <item>
///     Endpoint slices that implement <see cref="ISystemAdminEndpoint" /> are
///     discovered with the normal <c>IEndpoint</c> registrations.
///   </item>
///   <item>
///     <c>SetupEndpoints.MapZeeqEndpoints</c> builds the authenticated
///     <c>/api/v1</c> root group, creates this <c>/admin</c> subgroup, and
///     passes that subgroup to every <see cref="ISystemAdminEndpoint" />.
///   </item>
///   <item>
///     Those endpoints map only their relative paths, for example
///     <c>organizations</c>; the effective route becomes
///     <c>/api/v1/admin/organizations</c>.
///   </item>
///   <item>
///     This extension attaches <see cref="SystemAdminRequirement" /> to the
///     whole subgroup with <c>RequireAuthorization</c>, so all child endpoints
///     inherit the same live system-admin authorization policy.
///   </item>
///   <item>
///     <see cref="SystemAdminAuthorizationHandler" /> evaluates that
///     requirement through <see cref="SystemAdminEvaluator" />, which checks the
///     current request principal against the live configured system-admin
///     subject allow-list rather than trusting stale role claims.
///   </item>
///   <item>
///     <see cref="HiddenAdminRouteMetadata" /> lets
///     <see cref="HiddenAdminAuthorizationResultHandler" /> return 404 for
///     failed admin authorization so admin-only routes are not advertised to
///     non-admin callers.
///   </item>
/// </list>
/// </remarks>
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
