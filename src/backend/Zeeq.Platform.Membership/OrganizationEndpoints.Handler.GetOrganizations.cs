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
        var creatorEmails = await store.FindUserEmailsByIdsAsync(
            orgs.Select(org => org.CreatedByUserId).Distinct().ToArray(),
            ct
        );
        var creatorDomains = orgs.ToDictionary(
            org => org.Id,
            org =>
                EmailDomainNormalizer.FromEmail(
                    creatorEmails.GetValueOrDefault(org.CreatedByUserId)
                )
        );
        var claimedDomains = await store.FindAutoInviteSameDomainClaimsAsync(
            creatorDomains.Values.OfType<string>().Distinct().ToArray(),
            ct
        );

        var responses = new List<OrganizationResponse>(memberships.Count);
        foreach (var membership in memberships)
        {
            var org = orgMap.GetValueOrDefault(membership.OrganizationId);
            if (org is null)
            {
                responses.Add(
                    new OrganizationResponse(
                        Id: membership.OrganizationId,
                        Slug: null,
                        DisplayName: "",
                        IconUrl: null,
                        Role: membership.Role,
                        CreatedAtUtc: membership.CreatedAtUtc,
                        ActivatedAtUtc: null,
                        AutoInviteSameDomainEnabled: false,
                        AutoInviteSameDomain: null,
                        AutoInviteDefaultRole: OrganizationSameDomainOnboardingStatusFactory.DefaultRole,
                        AutoInviteSameDomainCanEnable: false,
                        AutoInviteSameDomainBlockReason: "organization_unavailable"
                    )
                );
                continue;
            }

            var creatorEmail = creatorEmails.GetValueOrDefault(org.CreatedByUserId);
            var creatorDomain = creatorDomains.GetValueOrDefault(org.Id);
            var domainAvailable =
                creatorDomain is null
                || !claimedDomains.TryGetValue(creatorDomain, out var claimingOrgId)
                || claimingOrgId == org.Id;
            var sameDomainStatus = OrganizationSameDomainOnboardingStatusFactory.Create(
                org,
                creatorEmail,
                domainAvailable
            );

            responses.Add(
                OrganizationSameDomainOnboardingStatusFactory.ToOrganizationResponse(
                    org,
                    membership.Role,
                    sameDomainStatus
                )
            );
        }

        return TypedResults.Ok<IReadOnlyList<OrganizationResponse>>(responses);
    }
}
