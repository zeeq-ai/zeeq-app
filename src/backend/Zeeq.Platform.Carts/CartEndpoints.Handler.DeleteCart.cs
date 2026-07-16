using Zeeq.Core.Carts;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Carts;

/// <summary>
/// Handler for cart deletion.
/// </summary>
public sealed class DeleteCartHandler(ICartStore store) : IEndpointHandler
{
    /// <summary>
    /// Deletes a saved cart.  Scoped by organization, owner, and cart id — users can only
    /// delete their own carts.  Returns 404 (not 403) when the cart doesn't exist or belongs
    /// to another owner, avoiding information leakage about other users' carts.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string orgId,
        string cartId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var ownerUserId = user.AsZeeqMinimalIdentity().OwnerUserId;
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.NotFound();
        }

        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return TypedResults.NotFound();
        }

        var deleted = await store.DeleteAsync(orgId, ownerUserId, cartId, ct);

        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
