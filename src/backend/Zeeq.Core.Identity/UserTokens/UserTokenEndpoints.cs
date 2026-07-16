using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Browser-authenticated endpoints for long-lived user-owned bearer tokens.
/// </summary>
/// <remarks>
/// The plaintext bearer token is returned only by the creation response generated
/// by OpenIddict. This endpoint stores metadata only so future list/revoke
/// operations do not require keeping a recoverable token value.
/// </remarks>
public sealed class UserTokenEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/tokens")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();

        group
            .MapGet(
                "/",
                static (
                    string orgId,
                    [FromServices] ListUserTokensHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(user, cancellationToken)
            )
            .WithName("GetUserTokens")
            .WithTags("Auth")
            .WithSummary("List your API tokens.")
            .WithDescription(
                """
                Returns the active long-lived bearer tokens owned by the authenticated user.
                Each entry exposes only metadata for the route organization (id, name, timestamps); the token value
                itself is not stored in recoverable form and is never returned here.

                Requires a signed-in browser session.
                """
            );

        group
            .MapPost(
                "/",
                static (
                    string orgId,
                    UserTokenCreateRequest request,
                    [FromServices] CreateUserTokenHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(request, user, cancellationToken)
            )
            .RequireActiveOrganization()
            .WithName("CreateUserToken")
            .WithTags("Auth")
            .WithSummary("Create an API token.")
            .WithDescription(
                """
                Issues a new long-lived bearer token for the authenticated user, suitable for
                scripting and CLI access in place of an interactive session in the route organization.

                The plaintext token value is returned **only** in this creation response and
                cannot be retrieved again; only metadata is persisted for later list and
                delete operations, so the caller must capture the value now.
                """
            );

        group
            .MapDelete(
                "/{id}",
                static (
                    string orgId,
                    string id,
                    [FromServices] RevokeUserTokenHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(id, user, cancellationToken)
            )
            .RequireActiveOrganization()
            .WithName("RevokeUserToken")
            .WithTags("Auth")
            .WithSummary("Delete an API token.")
            .WithDescription(
                """
                Deletes the long-lived bearer token metadata identified by `id`. The token stops
                authenticating on subsequent requests because validation requires a metadata row.

                Only tokens owned by the authenticated user can be deleted.
                """
            );
    }
}
