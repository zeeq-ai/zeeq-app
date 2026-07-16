namespace Zeeq.Platform.Membership;

/// <summary>
/// Declines a pending invitation after verifying the invitation belongs
/// to the caller (matched by email claim).
/// </summary>
public sealed class DeclineInvitationHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Verifies the invitation's <c>InvitedEmail</c> matches the caller's
    /// email claim, then transitions status to <c>declined</c>.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string invitationId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var email =
            user.FindFirstValue(OpenIddictConstants.Claims.Email)
            ?? user.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrWhiteSpace(email))
            return TypedResults.NotFound();

        // Ownership gate: only decline invitations addressed to the caller
        var invitations = await store.ListPendingInvitationsForEmailAsync(email, ct);

        if (!invitations.Any(i => i.Id == invitationId))
            return TypedResults.NotFound();

        // Atomically: Status → Declined, set DisabledAtUtc
        var updated = await store.DeclineInvitationAsync(invitationId, ct);

        return updated ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
