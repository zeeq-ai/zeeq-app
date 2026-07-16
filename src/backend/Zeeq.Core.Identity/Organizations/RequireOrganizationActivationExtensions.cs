using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Endpoint-convention helpers for organization route/session enforcement.
/// </summary>
public static class RequireOrganizationActivationExtensions
{
    extension(RouteGroupBuilder builder)
    {
        /// <summary>
        /// Requires the <c>{orgId}</c> route value to match the session's active organization.
        /// </summary>
        public RouteGroupBuilder RequireRouteOrganizationMatchesCookie()
        {
            builder.AddEndpointFilter<RequireRouteOrganizationMatchesCookieFilter>();

            return builder;
        }

        /// <summary>
        /// Requires the route-scoped organization to be active before the endpoint runs.
        /// </summary>
        public RouteGroupBuilder RequireActiveOrganization()
        {
            builder.AddEndpointFilter<RequireActiveOrganizationFilter>();

            return builder;
        }

        /// <summary>
        /// Requires the session's current organization to be active before the endpoint runs.
        /// </summary>
        public RouteGroupBuilder RequireActiveCurrentOrganization()
        {
            builder.AddEndpointFilter<RequireActiveCurrentOrganizationFilter>();

            return builder;
        }
    }

    extension(RouteHandlerBuilder builder)
    {
        /// <summary>
        /// Requires the <c>{orgId}</c> route value to match the session's active organization.
        /// </summary>
        public RouteHandlerBuilder RequireRouteOrganizationMatchesCookie()
        {
            builder.AddEndpointFilter<RequireRouteOrganizationMatchesCookieFilter>();

            return builder;
        }

        /// <summary>
        /// Requires the route-scoped organization to be active before the endpoint runs.
        /// </summary>
        public RouteHandlerBuilder RequireActiveOrganization()
        {
            builder.AddEndpointFilter<RequireActiveOrganizationFilter>();

            return builder;
        }

        /// <summary>
        /// Requires the session's current organization to be active before the endpoint runs.
        /// </summary>
        public RouteHandlerBuilder RequireActiveCurrentOrganization()
        {
            builder.AddEndpointFilter<RequireActiveCurrentOrganizationFilter>();

            return builder;
        }
    }
}
