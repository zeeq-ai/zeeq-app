using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Endpoints for the findings cart.
/// </summary>
public sealed class CartEndpoints : IEndpoint
{
    /// <summary>
    /// Route segment for a <c>{cartId}</c> parameter with regex validation matching.
    /// </summary>
    private const string CartIdRoute = @"{cartId:regex(^[a-z]{{6}}-[a-z]{{4}}-[a-z0-9]{{10}}$)}";

    /// <summary>
    /// Maps the endpoints for the findings cart.  This provides the UI functionality
    /// to manage the users' carts of findings which they can then interact with
    /// the local coding agent.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/carts")
            .WithTags("Carts")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}/carts — startup/list with summary items only (no full body payload).
        group
            .MapGet(
                "/",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListCartsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("ListCarts")
            .Produces<CartListResponse>()
            .Produces<CartError>(StatusCodes.Status400BadRequest)
            .WithSummary("List saved findings carts.")
            .WithDescription(
                """
                Returns the caller's saved carts ordered newest-saved first. Each cart includes
                only lightweight item summaries — hash, title, facet, summary, criticality, and
                annotation — not full finding body or card payloads. Safe to call at application
                startup. Scoped to the caller's active organization.
                """
            )
            .RequireActiveOrganization();

        // POST /api/v1/orgs/{orgId}/carts — first-time save of a client-built draft cart.
        group
            .MapPost(
                "/",
                static (
                    string orgId,
                    SaveCartRequest request,
                    ClaimsPrincipal user,
                    [FromServices] SaveCartHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .WithName("SaveCart")
            .Produces<CartResponse>(StatusCodes.Status201Created)
            .Produces<CartError>(StatusCodes.Status400BadRequest)
            .Produces<CartError>(StatusCodes.Status409Conflict)
            .WithSummary("Save a draft cart.")
            .WithDescription(
                """
                Persists a client-built draft cart for the first time. The request carries the
                client-generated id, generated name, original draft creation timestamp, and all
                finding snapshots. Both lightweight summaries (for list UI) and full payloads
                (for text/MCP compilation) are stored.

                Enforces a 5 saved-cart limit per owner (returns 409 Conflict when reached)
                and a 10-item limit per cart (returns 400 Bad Request when exceeded). Saved
                carts are immutable server-side — there is no update or patch endpoint; editing
                a saved cart is done client-side via copy-to-new-draft followed by delete.
                """
            )
            .RequireActiveOrganization();

        // DELETE /api/v1/orgs/{orgId}/carts/{cartId:regex(...)}
        group
            .MapDelete(
                $"/{CartIdRoute}",
                static (
                    string orgId,
                    string cartId,
                    ClaimsPrincipal user,
                    [FromServices] DeleteCartHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, cartId, user, ct)
            )
            .WithName("DeleteCart")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Delete a saved cart.")
            .WithDescription(
                """
                Removes a saved cart by id. Scoped to the caller's active organization and
                owner identity — carts owned by other users in the same organization cannot be
                deleted by this caller. Returns 404 if the cart does not exist or is not owned
                by the caller.
                """
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/carts/{cartId:regex(...)}/text — agent-ready instructions text.
        group
            .MapGet(
                $"/{CartIdRoute}/text",
                static (
                    string orgId,
                    string cartId,
                    ClaimsPrincipal user,
                    [FromServices] GetCartTextHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, cartId, user, ct)
            )
            .WithName("GetCartText")
            .Produces<CartTextResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get cart instructions text.")
            .WithDescription(
                """
                Compiles a saved cart's full findings payload into the agent-ready
                <instruction_for_agents> XML block. Each finding includes
                repo/PR provenance, reviewer facet and agent, the finding body as CDATA, and
                the optional per-item annotation as a note attribute. Uses the same builder as
                the get_cart_findings MCP tool. Only returns text for saved (server-persisted)
                carts. The caller is expected to paste this text into a local agent chat or
                pass it to an MCP session.
                """
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/carts/{cartId:regex(...)}/copy-source — full saved cart items for copy-to-new-draft.
        group
            .MapGet(
                $"/{CartIdRoute}/copy-source",
                static (
                    string orgId,
                    string cartId,
                    ClaimsPrincipal user,
                    [FromServices] GetCartCopySourceHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, cartId, user, ct)
            )
            .WithName("GetCartCopySource")
            .Produces<CartCopySourceResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Copy a saved cart to a new draft.")
            .WithDescription(
                """
                Returns the full item payload of a saved cart, including finding bodies, repo/PR
                provenance, and annotations. Intended solely for the client-side "Copy to new
                draft" action — the client creates a new local draft with a fresh ID and
                generated name from this payload. Saved carts are never edited in place: users
                copy to a new draft, make changes locally, then save that as a new cart and
                optionally delete the original.
                """
            )
            .RequireActiveOrganization();
    }
}
