namespace Zeeq.Core.Carts;

/// <summary>
/// Thrown when an initial save would contain more than <see cref="CartLimits.MaxItemsPerCart"/>
/// items. Defense-in-depth only — the client already enforces this before enabling Save.
/// </summary>
public sealed class CartItemLimitExceededException(int itemCount)
    : Exception($"Cart has {itemCount} items; the maximum is {CartLimits.MaxItemsPerCart}.");
