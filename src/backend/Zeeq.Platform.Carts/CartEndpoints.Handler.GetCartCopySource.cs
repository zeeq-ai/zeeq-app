using Zeeq.Core.Carts;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Handles cart copies.
/// </summary>
public sealed class GetCartCopySourceHandler(ICartStore store) : IEndpointHandler
{
    /// <summary>
    /// Returns the full saved items for the given cart, scoped to the caller's organization.
    /// Only used for the explicit "Copy to new draft" action — the startup list endpoint
    /// never exposes the full finding body.
    /// </summary>
    public async Task<Results<Ok<CartCopySourceResponse>, NotFound>> HandleAsync(
        string orgId,
        string cartId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.NotFound();
        }

        var cart = await store.FindAsync(orgId, cartId, ct);
        if (cart is null)
        {
            return TypedResults.NotFound();
        }

        var items = cart
            .ItemsPayload.Select(item => new SaveCartItemRequest(
                item.Hash,
                item.Title,
                item.Criticality,
                item.File,
                item.Line,
                item.Side,
                item.Summary,
                item.Body,
                item.OwnerQualifiedRepoName,
                item.PullRequestNumber,
                item.Facet,
                item.Agent,
                item.Annotation,
                item.AddedAtUtc
            ))
            .ToArray();

        return TypedResults.Ok(new CartCopySourceResponse(cart.Id, cart.Name, items));
    }
}
