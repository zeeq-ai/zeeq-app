namespace Zeeq.Platform.Membership;

/// <summary>
/// Lists active members of an organization with user display info.
/// Used by the Settings → Members view. Requires the caller to be a
/// member of the target org.
/// </summary>
public sealed class ListMembersHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Verifies the caller's membership in the target org, then delegates
    /// to the store's read-model projection (joins users for display info).
    /// </summary>
    public async Task<Results<Ok<IReadOnlyList<MemberResponse>>, NotFound>> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        // Gate: caller must be an active member of this org
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);

        if (!memberships.Any(m => m.OrganizationId == orgId))
            return TypedResults.NotFound();

        // Delegate to store — the query joins users for display info
        var members = await store.ListMembersForOrganizationAsync(orgId, ct);

        return TypedResults.Ok<IReadOnlyList<MemberResponse>>([
            .. members.Select(m => new MemberResponse(
                m.UserId,
                m.DisplayName,
                m.Email,
                m.PictureUrl,
                m.Role,
                m.JoinedAtUtc
            )),
        ]);
    }
}
