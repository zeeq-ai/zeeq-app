namespace Zeeq.Platform.Membership;

/// <summary>
/// Disables a membership without deleting the row — preserves audit
/// history. Verifies the target user is actually a member first.
/// </summary>
public sealed class RemoveMemberHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Soft-deletes a membership after verifying the target user is a
    /// member. Preserves audit history.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string orgId,
        string userId,
        CancellationToken ct
    )
    {
        // Verify the target user is a member of this org
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);

        if (!memberships.Any(m => m.OrganizationId == orgId))
        {
            return TypedResults.NotFound();
        }

        // Soft-delete: Status → Disabled, record timestamp, clear default flag
        await store.RemoveMemberAsync(orgId, userId, ct);

        return TypedResults.NoContent();
    }
}
