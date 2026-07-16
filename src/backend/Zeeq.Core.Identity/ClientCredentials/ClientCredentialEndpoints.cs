using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Zeeq.Core.Identity;

/// <summary>
/// Browser-authenticated endpoints for user-owned OAuth client credentials.
/// </summary>
/// <remarks>
/// These endpoints intentionally live outside Dynamic Client Registration.
/// DCR remains public/native/PKCE-only; this API creates confidential clients
/// after a user has signed in with an external IdP.
/// </remarks>
public sealed partial class ClientCredentialEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/clients")
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
                    [FromServices] ListClientCredentialsHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(user, cancellationToken)
            )
            .WithName("GetClientCredentials")
            .WithTags("Auth")
            .WithSummary("List your client credentials.")
            .WithDescription(
                """
                Returns the active confidential OAuth client credentials owned by the
                authenticated user in the route organization. Each entry includes the `client_id` and descriptive
                metadata; the `client_secret` is never returned by this endpoint.

                Requires a signed-in browser session.
                """
            );

        group
            .MapPost(
                "/",
                static (
                    string orgId,
                    ClientCredentialCreateRequest request,
                    [FromServices] CreateClientCredentialHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(request, user, cancellationToken)
            )
            .RequireActiveOrganization()
            .WithName("CreateClientCredential")
            .WithTags("Auth")
            .WithSummary("Create a client credential.")
            .WithDescription(
                """
                Provisions a new confidential OAuth client owned by the authenticated user.
                Unlike Dynamic Client Registration (which stays public/PKCE-only), this
                creates a secret-bearing client for a user who has already signed in with an
                external identity provider in the route organization.

                The response carries the `client_id` and metadata. The generated
                `client_secret` is shown only at creation time and cannot be recovered later,
                so it must be stored by the caller.
                """
            );

        group
            .MapDelete(
                "/{clientId}",
                static (
                    string orgId,
                    string clientId,
                    [FromServices] RevokeClientCredentialHandler handler,
                    ClaimsPrincipal user,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(clientId, user, cancellationToken)
            )
            .RequireActiveOrganization()
            .WithName("RevokeClientCredential")
            .WithTags("Auth")
            .WithSummary("Delete a client credential.")
            .WithDescription(
                """
                Deletes the confidential OAuth client identified by `clientId`.
                The credential can no longer issue new tokens. Already-issued self-contained
                access tokens remain valid until their normal expiry window.

                Only credentials owned by the authenticated user can be deleted.
                """
            );
    }
}
