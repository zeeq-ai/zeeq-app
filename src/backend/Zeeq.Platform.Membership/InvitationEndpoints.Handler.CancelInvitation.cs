namespace Zeeq.Platform.Membership;

/// <summary>
/// Cancels a pending organization invitation.
/// </summary>
public sealed class CancelInvitationHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Verifies the caller belongs to the organization, then soft-cancels the
    /// pending invitation row so audit history is preserved.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string orgId,
        string invitationId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);

        if (!memberships.Any(m => m.OrganizationId == orgId))
        {
            return TypedResults.NotFound();
        }

        var canceled = await store.CancelInvitationAsync(orgId, invitationId, ct);

        return canceled ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
