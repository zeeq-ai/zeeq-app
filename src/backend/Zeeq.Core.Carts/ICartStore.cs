namespace Zeeq.Core.Carts;

/// <summary>
/// Domain-slice interface for cart row storage.  API handlers and the MCP tool depend on
/// this abstraction instead of the Postgres <c>DbContext</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>No update method.</b>  Saved carts are immutable server-side — the only mutations
/// are create (once, from a client draft) and delete.  Draft edits happen in the browser;
/// reopening a saved cart works by copying it to a new draft, which is a create, not an update.
/// </para>
/// <para>
/// <b>Scoping rules.</b>  <see cref="ListForOwnerAsync"/> and <see cref="DeleteAsync"/>
/// are owner-scoped (the caller can only see/delete their own carts).
/// <see cref="FindAsync"/> is org-scoped — it only checks the organization, not the owner.
/// This is deliberate: the MCP <c>get_cart_findings</c> tool looks up by cart id alone
/// because the agent's session may carry a different <c>sub</c> than the browser session
/// that saved the cart.
/// </para>
/// </remarks>
public interface ICartStore
{
    /// <summary>
    /// Persists a fully-built draft cart for the first time.  <paramref name="cart"/>'s
    /// <see cref="Cart.Id"/> is client-supplied (<c>generateCartName()</c>, e.g.
    /// <c>snappy-lake-a1b2c3d4e5</c>) — the store guards against unique-violation
    /// collisions defensively even though a collision is astronomically unlikely.
    /// </summary>
    /// <exception cref="CartLimitExceededException">
    /// Thrown when the owner is already at <see cref="CartLimits.MaxCartsPerOwner"/>.
    /// </exception>
    Task<Cart> CreateAsync(Cart cart, CancellationToken ct);

    /// <summary>
    /// Lists an owner's saved carts, newest-saved first.  Returns the full row including
    /// <see cref="Cart.ItemsPayload"/>, but callers (`ListCartsHandler`)
    /// project only the summary fields into the response DTO.
    /// </summary>
    Task<IReadOnlyList<Cart>> ListForOwnerAsync(
        string organizationId,
        string ownerUserId,
        CancellationToken ct
    );

    /// <summary>
    /// Finds a cart by organization + id only — no owner filter.  This is org-scoped,
    /// not owner-scoped, because MCP retrieval uses the cart id as a shared capability.
    /// See `CartMcpTools.GetCartFindings`.
    /// </summary>
    Task<Cart?> FindAsync(string organizationId, string cartId, CancellationToken ct);

    /// <summary>
    /// Deletes a cart row.  Scoped by organization, owner, and cart id.
    /// Returns <c>false</c> if the row did not exist.
    /// </summary>
    Task<bool> DeleteAsync(
        string organizationId,
        string ownerUserId,
        string cartId,
        CancellationToken ct
    );
}
