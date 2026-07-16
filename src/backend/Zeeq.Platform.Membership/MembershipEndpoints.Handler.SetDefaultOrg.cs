namespace Zeeq.Platform.Membership;

/// <summary>
/// Sets an organization as the user's default. Atomically unsets any
/// existing default via the store's transactional implementation.
/// </summary>
public sealed class SetDefaultOrgHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Verifies membership and delegates to the store's transactional
    /// default-org swap.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        // Verify the user is a member of the target org
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);

        if (!memberships.Any(m => m.OrganizationId == orgId))
            return TypedResults.NotFound();

        // Atomically unset previous default + set new one in a single transaction
        await store.SetDefaultOrganizationAsync(userId, orgId, ct);

        return TypedResults.NoContent();
    }
}
