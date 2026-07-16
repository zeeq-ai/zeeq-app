namespace Zeeq.Core.Carts;

/// <summary>
/// Thrown by <see cref="ICartStore.CreateAsync"/> when the owner already has
/// <see cref="CartLimits.MaxCartsPerOwner"/> saved server-side carts. Draft carts are
/// browser-local and are not part of the authoritative server cap.
/// </summary>
public sealed class CartLimitExceededException(string organizationId, string ownerUserId)
    : Exception(
        $"Owner '{ownerUserId}' in organization '{organizationId}' already has "
            + $"{CartLimits.MaxCartsPerOwner} carts."
    );
