using Zeeq.Core.Carts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zeeq.Data.Postgres.Carts;

/// <summary>
/// Postgres implementation of <see cref="ICartStore"/>.  Carts are stored in an
/// unlogged table with a 5-cart-per-owner cap, client-generated ids, and org/owner
/// scoping on every query.
/// </summary>
/// <remarks>
/// <b>Limit enforcement.</b>  <see cref="CreateAsync"/> counts the owner's existing
/// rows before inserting.  This is a read-then-write check — two concurrent saves could
/// both pass the count check.  We accept this rare race because the UX already enforces
/// the 5-cart limit client-side, and the consequence is one extra cart row, not data
/// corruption.
/// </remarks>
internal sealed class PostgresCartStore(PostgresDbContext db) : ICartStore
{
    public async Task<Cart> CreateAsync(Cart cart, CancellationToken ct)
    {
        var count = await db
            .Carts.TagWithOperationCallSite("cart.count_for_owner")
            .CountAsync(
                c => c.OrganizationId == cart.OrganizationId && c.OwnerUserId == cart.OwnerUserId,
                ct
            );

        if (count >= CartLimits.MaxCartsPerOwner)
        {
            throw new CartLimitExceededException(cart.OrganizationId, cart.OwnerUserId);
        }

        db.Carts.Add(cart);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new InvalidOperationException(
                $"Cart id '{cart.Id}' already exists — the client-generated id collided."
            );
        }

        return cart;
    }

    public async Task<IReadOnlyList<Cart>> ListForOwnerAsync(
        string organizationId,
        string ownerUserId,
        CancellationToken ct
    ) =>
        await db
            .Carts.TagWithOperationCallSite("cart.list_for_owner")
            .Where(cart => cart.OrganizationId == organizationId && cart.OwnerUserId == ownerUserId)
            .OrderByDescending(cart => cart.UpdatedAtUtc)
            .ToArrayAsync(ct);

    public async Task<Cart?> FindAsync(
        string organizationId,
        string cartId,
        CancellationToken ct
    ) =>
        await db
            .Carts.TagWithOperationCallSite("cart.find")
            .SingleOrDefaultAsync(
                cart => cart.OrganizationId == organizationId && cart.Id == cartId,
                ct
            );

    public async Task<bool> DeleteAsync(
        string organizationId,
        string ownerUserId,
        string cartId,
        CancellationToken ct
    )
    {
        var deleted = await db
            .Carts.TagWithOperationCallSite("cart.delete")
            .Where(cart =>
                cart.OrganizationId == organizationId
                && cart.OwnerUserId == ownerUserId
                && cart.Id == cartId
            )
            .ExecuteDeleteAsync(ct);

        return deleted > 0;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException
            is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
