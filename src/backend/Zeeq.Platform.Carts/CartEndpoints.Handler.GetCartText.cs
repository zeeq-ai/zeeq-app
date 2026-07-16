using Zeeq.Core.Carts;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Handles cart instruction text.
/// </summary>
public sealed class GetCartTextHandler(ICartStore store) : IEndpointHandler
{
    /// <summary>
    /// Returns a short MCP invocation instruction for a saved cart.  The instruction
    /// tells the agent to call <c>get_cart_findings</c> with the cart ID; the full
    /// findings XML is returned by the MCP tool itself.
    /// </summary>
    public async Task<Results<Ok<CartTextResponse>, NotFound>> HandleAsync(
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

        return TypedResults.Ok(new CartTextResponse(cartId.ToMcpInstructions()));
    }
}
