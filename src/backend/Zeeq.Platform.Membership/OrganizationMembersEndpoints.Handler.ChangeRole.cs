namespace Zeeq.Platform.Membership;

/// <summary>
/// Changes a member's role within an organization. Only valid roles
/// (<c>owner</c>, <c>admin</c>, <c>member</c>) are accepted.
/// </summary>
public sealed class ChangeMemberRoleHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Validates the role value and the member's existence, then delegates
    /// to the store for an ExecuteUpdateAsync mutation.
    /// </summary>
    public async Task<Results<NoContent, NotFound, BadRequest>> HandleAsync(
        string orgId,
        string userId,
        string newRole,
        CancellationToken ct
    )
    {
        // Only allow valid role names
        if (newRole is not ("owner" or "admin" or "member"))
            return TypedResults.BadRequest();

        // Verify the target user is actually a member of this org
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);

        if (!memberships.Any(m => m.OrganizationId == orgId))
        {
            return TypedResults.NotFound();
        }

        await store.UpdateMemberRoleAsync(orgId, userId, newRole, ct);

        return TypedResults.NoContent();
    }
}
