using Zeeq.Core.Carts;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Handles cart instruction text.
/// </summary>
public sealed class GetCartTextHandler(
    ICartStore store,
    ICodeReviewFindingTriageStore triages,
    CodeReviewRequestLinkFactory links
) : IEndpointHandler
{
    /// <summary>
    /// Returns cart findings with live triage annotations.
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

        var text = await CartInstructionsTextBuilder.ToAgentInstructionsAsync(
            cart.ItemsPayload,
            orgId,
            triages,
            links,
            cancellationToken: ct
        );
        return TypedResults.Ok(new CartTextResponse(text));
    }
}
