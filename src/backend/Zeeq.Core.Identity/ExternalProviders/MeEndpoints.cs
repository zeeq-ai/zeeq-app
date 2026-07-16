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
    }
}
