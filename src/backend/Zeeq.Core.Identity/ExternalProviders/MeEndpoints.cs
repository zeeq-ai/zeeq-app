using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Browser-authenticated endpoint for the current user's application identity.
/// </summary>
public sealed class MeEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        // GET /api/v1/me
        app.MapGet(
                "me",
                static (
                    [FromServices] GetMeHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) => handler.HandleAsync(user, ct)
            )
            .WithName("GetMe")
            .WithTags("Auth")
            .WithSummary("Get the current user.")
            .WithDescription(
                """
                Returns the authenticated caller's identity for the current session: profile
                claims (name, email, picture, provider) plus the active organization, team,
                and slug decoded from the encrypted, server-issued session token.

                The caller's role, full organization list, and pending invitations are
                resolved fresh from the membership store on each request rather than read
                from the token, so role and membership changes take effect immediately.
                Requires a signed-in browser session.
                """
            )
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireActiveCurrentOrganization();

        // GET /api/v1/orgs/{orgId}/me/aliases
        app.MapGet(
                "orgs/{orgId}/me/aliases",
                static (
                    string orgId,
                    [FromServices] GetUserAliasesHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("GetUserAliases")
            .WithTags("Auth")
            .WithSummary("Get my organization-scoped aliases.")
            .WithDescription(
                """
                Returns aliases owned by the signed-in user in the active organization.
                Email aliases match agent telemetry owner emails; GitHub aliases match
                provider logins on pull requests and code review records.
                """
            )
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie()
            .RequireActiveOrganization();

        // PUT /api/v1/orgs/{orgId}/me/aliases
        app.MapPut(
                "orgs/{orgId}/me/aliases",
                static (
                    string orgId,
                    [FromBody] UpdateUserAliasesRequest request,
                    [FromServices] UpdateUserAliasesHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .WithName("UpdateUserAliases")
            .WithTags("Auth")
            .WithSummary("Update my organization-scoped aliases.")
            .WithDescription(
                """
                Replaces the signed-in user's aliases in the active organization.
                Aliases are organization-scoped so the same raw identity can be mapped
                differently in different organizations when needed.
                """
            )
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie()
            .RequireActiveOrganization();
    }
}
