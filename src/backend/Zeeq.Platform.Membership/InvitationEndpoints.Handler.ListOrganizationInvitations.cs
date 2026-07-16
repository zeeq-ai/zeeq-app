namespace Zeeq.Platform.Membership;

/// <summary>
/// Lists pending invitations that have been sent for an organization.
/// </summary>
public sealed class ListOrganizationInvitationsHandler(IZeeqMembershipStore store)
    : IEndpointHandler
{
    /// <summary>
    /// Verifies the caller belongs to the organization, then returns pending
    /// outgoing invitations for the member-management UI.
    /// </summary>
    public async Task<Results<Ok<IReadOnlyList<InvitationResponse>>, NotFound>> HandleAsync(
        string orgId,
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

        var invitations = await store.ListPendingInvitationsForOrganizationAsync(orgId, ct);
        var org = await store.FindOrganizationByIdAsync(orgId, ct);

        return TypedResults.Ok<IReadOnlyList<InvitationResponse>>([
            .. invitations.Select(i => new InvitationResponse(
                i.Id,
                i.OrganizationId,
                org?.DisplayName,
                i.InvitedEmail,
                i.Role,
                i.CreatedAtUtc,
                i.ExpiresAtUtc!.Value
            )),
        ]);
    }
}
