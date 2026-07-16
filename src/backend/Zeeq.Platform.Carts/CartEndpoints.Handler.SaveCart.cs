using Zeeq.Core.Carts;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Handler for saving user carts of findings.
/// </summary>
public sealed class SaveCartHandler(ICartStore store) : IEndpointHandler
{
    /// <summary>
    /// Persists a client-built draft cart, making it durable and MCP-retrievable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Validation chain.</b>  Active org → owner user → non-empty id/name → non-empty
    /// items → 10-item cap → 5-cart cap (enforced by the store, surfaced as 409).
    /// Each guard returns a distinct error code so the frontend can provide targeted
    /// feedback.
    /// </para>
    /// <para>
    /// <b>Dual JSON population.</b>  <see cref="CartEndpointMapping.ToCart"/> builds both
    /// <see cref="Cart.ItemSummaries"/> and <see cref="Cart.ItemsPayload"/> from the single
    /// save request — see the mapping class for details.
    /// </para>
    /// </remarks>
    public async Task<
        Results<Created<CartResponse>, BadRequest<CartError>, Conflict<CartError>>
    > HandleAsync(string orgId, SaveCartRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var (_, teamId, ownerUserId) = user.AsZeeqMinimalIdentity();
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

        if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest(
                new CartError("invalid_request", "id and name are required.")
            );
        }

        if (request.Items.Count == 0)
        {
            return TypedResults.BadRequest(
                new CartError("empty_cart", "Cannot save an empty cart.")
            );
        }

        if (request.Items.Count > CartLimits.MaxItemsPerCart)
        {
            return TypedResults.BadRequest(
                new CartError(
                    "cart_item_limit_reached",
                    $"A cart may contain at most {CartLimits.MaxItemsPerCart} findings."
                )
            );
        }

        var cart = request.ToCart(orgId, teamId, ownerUserId);

        try
        {
            var created = await store.CreateAsync(cart, ct);

            return TypedResults.Created(
                $"/api/v1/orgs/{Uri.EscapeDataString(orgId)}/carts/{created.Id}",
                created.ToResponse()
            );
        }
        catch (CartLimitExceededException)
        {
            return TypedResults.Conflict(
                new CartError(
                    "cart_limit_reached",
                    $"You already have {CartLimits.MaxCartsPerOwner} saved carts. Delete one to save another."
                )
            );
        }
    }
}
