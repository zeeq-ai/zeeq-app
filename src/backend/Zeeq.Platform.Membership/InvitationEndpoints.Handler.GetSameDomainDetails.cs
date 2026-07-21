namespace Zeeq.Platform.Membership;

/// <summary>
/// Returns same-domain invitation details after verifying caller ownership by email.
/// </summary>
public sealed class GetSameDomainInvitationDetailsHandler(IZeeqMembershipStore store)
    : IEndpointHandler
{
    /// <summary>
    /// Returns organization and owner display data for the standalone join-your-team screen.
    /// </summary>
    public async Task<Results<Ok<SameDomainInvitationDetailsResponse>, NotFound>> HandleAsync(
        string invitationId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var email =
            user.FindFirstValue(OpenIddictConstants.Claims.Email)
            ?? user.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrWhiteSpace(email))
        {
            return TypedResults.NotFound();
        }

        var details = await store.FindSameDomainInvitationDetailsAsync(invitationId, email, ct);

        return details is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(SameDomainInvitationDetailsResponse.From(details));
    }
}
