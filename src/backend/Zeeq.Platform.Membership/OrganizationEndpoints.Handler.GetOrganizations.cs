namespace Zeeq.Platform.Membership;

/// <summary>
/// Lists all organizations the authenticated user belongs to.
/// Returns the same <c>orgs[]</c> shape as the /me response.
/// </summary>
public sealed class GetOrganizationsHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Returns all orgs the user belongs to. Uses two queries:
    /// memberships for roles, then batch org lookup for slug + display name.
    /// </summary>
    public async Task<Ok<IReadOnlyList<OrganizationResponse>>> HandleAsync(
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        // Resolve user identity from the authenticated principal
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;

        // Query 1: get all memberships for this user (role + org ID)
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);

        // Query 2: batch-resolve slug + display name for each org
        var orgIds = memberships.Select(m => m.OrganizationId).ToArray();
        var orgs = await store.FindOrganizationsByIdsAsync(orgIds, ct);
        var orgMap = orgs.ToDictionary(o => o.Id);

        return TypedResults.Ok<IReadOnlyList<OrganizationResponse>>(
            memberships
                .Select(m =>
                {
                    var org = orgMap.GetValueOrDefault(m.OrganizationId);
                    return new OrganizationResponse(
                        Id: m.OrganizationId,
                        Slug: org?.Slug,
                        DisplayName: org?.DisplayName ?? "",
                        IconUrl: org?.IconUrl,
                        Role: m.Role,
                        CreatedAtUtc: m.CreatedAtUtc,
                        ActivatedAtUtc: org?.ActivatedAtUtc
                    );
                })
                .ToArray()
        );
    }
}
