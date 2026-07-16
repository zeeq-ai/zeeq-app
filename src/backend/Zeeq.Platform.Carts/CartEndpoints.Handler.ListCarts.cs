using Zeeq.Core.Carts;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Handler for listing user carts.
/// </summary>
public sealed class ListCartsHandler(ICartStore store) : IEndpointHandler
{
    /// <summary>
    /// Lists the caller's saved findings carts.  Returns <see cref="CartFindingResponse"/>
    /// items (summary-only — hash, title, facet, summary, criticality, annotation).  The
    /// full <see cref="Cart.ItemsPayload"/> is never exposed through this endpoint; the
    /// browser calls this on application startup, and we must not fetch full finding bodies
    /// for every saved cart on every page load.
    /// </summary>
    public async Task<Results<Ok<CartListResponse>, BadRequest<CartError>>> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var ownerUserId = user.AsZeeqMinimalIdentity().OwnerUserId;
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(
                new CartError("missing_organization", "Active organization is required.")
            );
        }

        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return TypedResults.BadRequest(
                new CartError("missing_owner", "Owner user id is required.")
            );
        }

        var carts = await store.ListForOwnerAsync(orgId, ownerUserId, ct);

        return TypedResults.Ok(new CartListResponse([.. carts.Select(cart => cart.ToResponse())]));
    }
}
