namespace Zeeq.Platform.Membership;

/// <summary>
/// Accepts an invitation and makes the invited organization the user's default.
/// </summary>
public sealed class AcceptInvitationAsDefaultHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Verifies the invitation belongs to the caller, then accepts it as the default organization.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string invitationId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var email =
            user.FindFirstValue(OpenIddictConstants.Claims.Email)
            ?? user.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrWhiteSpace(email))
        {
            return TypedResults.NotFound();
        }

        var invitations = await store.ListPendingInvitationsForEmailAsync(email, ct);
        if (!invitations.Any(invitation => invitation.Id == invitationId))
        {
            return TypedResults.NotFound();
        }

        var updated = await store.AcceptInvitationAsDefaultAsync(invitationId, userId, ct);

        return updated ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
