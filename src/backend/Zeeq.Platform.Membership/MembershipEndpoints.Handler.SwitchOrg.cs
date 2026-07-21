namespace Zeeq.Platform.Membership;

/// <summary>
/// Validates membership in the target org and builds a new claims
/// principal with updated tenant claims. The caller is responsible
/// for calling <c>SignInAsync</c> with the returned principal.
/// </summary>
public sealed class SwitchOrgHandler(
    IZeeqMembershipStore store,
    SystemAdminEvaluator systemAdminEvaluator
) : IEndpointHandler
{
    /// <summary>
    /// Verifies membership in the target org and builds a new principal
    /// with updated tenant + routing claims.
    /// </summary>
    public async Task<Results<Ok<ClaimsPrincipal>, NotFound>> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;

        // Verify the user is a member of the target org
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);
        var target = memberships.FirstOrDefault(m => m.OrganizationId == orgId);

        if (target is null)
        {
            return TypedResults.NotFound();
        }

        // Resolve the target org before writing tenant claims. Active membership
        // alone is not enough: inactive personal orgs can exist before billing or
        // activation, and switching into one would poison the user's session.
        var org = await store.FindOrganizationByIdAsync(orgId, ct);
        if (org is null || org.ActivatedAtUtc is null || org.DisabledAtUtc is not null)
        {
            return TypedResults.NotFound();
        }

        var orgSlug = org?.Slug;

        var teamId = await store.FindRootTeamIdForMemberAsync(orgId, userId, ct);

        if (teamId is null)
        {
            return TypedResults.NotFound();
        }

        var newPrincipal = ExternalUserPrincipalFactory.CreateCookiePrincipal(
            new AuthContext(userId, orgId, teamId),
            user.FindFirstValue(AuthClaims.Provider) ?? "",
            user.FindFirstValue(AuthClaims.ProviderSubject) ?? "",
            user.FindFirstValue(OpenIddictConstants.Claims.Name),
            user.FindFirstValue(OpenIddictConstants.Claims.Email),
            user.FindFirstValue(AuthClaims.Picture),
            orgSlug,
            orgRole: target.Role,
            isSystemAdmin: systemAdminEvaluator.IsSystemAdmin(user)
        );

        return TypedResults.Ok(newPrincipal);
    }
}
