namespace Zeeq.Platform.Membership;

/// <summary>
/// Accepts a pending invitation after verifying the invitation belongs
/// to the caller (matched by email claim).
/// </summary>
public sealed class AcceptInvitationHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Verifies the invitation's <c>InvitedEmail</c> matches the caller's
    /// email claim, then binds the authenticated user's identity and
    /// transitions status to <c>active</c>.
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
            return TypedResults.NotFound();

        // Ownership gate: only accept invitations addressed to the caller
        var invitations = await store.ListPendingInvitationsForEmailAsync(email, ct);

        if (!invitations.Any(i => i.Id == invitationId))
            return TypedResults.NotFound();

        // Atomically: set UserId, Status → Active, clear expiry
        var updated = await store.AcceptInvitationAsync(invitationId, userId, ct);

        return updated ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
