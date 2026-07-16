namespace Zeeq.Platform.Membership;

/// <summary>
/// Lists pending invitations for the current user, looked up by their
/// email claim. Resolves organization display names for the list UI.
/// </summary>
public sealed class ListInvitationsHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Resolves the user's pending invitations by email and batches
    /// organization display names for the list UI.
    /// </summary>
    public async Task<Ok<IReadOnlyList<InvitationResponse>>> HandleAsync(
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        // Look up invitations by the user's email claim
        var email =
            user.FindFirstValue(OpenIddict.Abstractions.OpenIddictConstants.Claims.Email)
            ?? user.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrWhiteSpace(email))
            return TypedResults.Ok<IReadOnlyList<InvitationResponse>>([]);

        // Resolve pending invitations and batch-lookup org display names
        var invitations = await store.ListPendingInvitationsForEmailAsync(email, ct);
        var orgIds = invitations.Select(i => i.OrganizationId).Distinct().ToArray();
        var orgs = orgIds.Length > 0 ? await store.FindOrganizationsByIdsAsync(orgIds, ct) : [];
        var orgMap = orgs.ToDictionary(o => o.Id);

        return TypedResults.Ok<IReadOnlyList<InvitationResponse>>(
            invitations
                .Select(i => new InvitationResponse(
                    i.Id,
                    i.OrganizationId,
                    orgMap.GetValueOrDefault(i.OrganizationId)?.DisplayName,
                    i.InvitedEmail,
                    i.Role,
                    i.CreatedAtUtc,
                    i.ExpiresAtUtc!.Value
                ))
                .ToArray()
        );
    }
}
