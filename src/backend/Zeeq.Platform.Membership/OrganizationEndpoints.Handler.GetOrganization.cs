namespace Zeeq.Platform.Membership;

/// <summary>
/// Returns a single organization's details plus the authenticated user's
/// role within it.
/// </summary>
public sealed class GetOrganizationHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Returns a single org's details plus the caller's resolved role.
    /// </summary>
    public async Task<Results<Ok<OrganizationResponse>, NotFound>> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;

        // Verify the caller belongs to this org — fail closed for non-members
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);
        var membership = memberships.FirstOrDefault(m => m.OrganizationId == orgId);
        if (membership is null)
            return TypedResults.NotFound();

        // Look up the org by ID
        var org = await store.FindOrganizationByIdAsync(orgId, ct);
        if (org is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(
            new OrganizationResponse(
                Id: org.Id,
                Slug: org.Slug,
                DisplayName: org.DisplayName,
                IconUrl: org.IconUrl,
                Role: membership.Role,
                CreatedAtUtc: org.CreatedAtUtc,
                ActivatedAtUtc: org.ActivatedAtUtc
            )
        );
    }
}
