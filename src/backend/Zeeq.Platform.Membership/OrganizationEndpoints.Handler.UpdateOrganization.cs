using System.Text.RegularExpressions;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Updates organization name, slug, or icon. Validates slug format and
/// uniqueness.
/// </summary>
public sealed partial class UpdateOrganizationHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Applies settings-form updates: display name, slug (with format +
    /// uniqueness validation), and icon. Returns the updated org with the
    /// caller's resolved role.
    /// </summary>
    public async Task<Results<Ok<OrganizationResponse>, NotFound, ValidationProblem>> HandleAsync(
        string orgId,
        UpdateOrganizationRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        // Gate: caller must be an active member of this org
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);
        var membership = memberships.FirstOrDefault(m => m.OrganizationId == orgId);

        if (membership is null)
            return TypedResults.NotFound();

        // Load existing org
        var org = await store.FindOrganizationByIdAsync(orgId, ct);

        if (org is null)
            return TypedResults.NotFound();

        // Apply display name if provided
        if (request.DisplayName is { Length: > 0 })
            org.DisplayName = request.DisplayName.Trim();

        // Validate and apply slug if provided
        if (request.Slug is { Length: > 0 })
        {
            var slug = request.Slug.Trim();

            if (!SlugRegex().IsMatch(slug))
            {
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["slug"] = ["Slug must be lowercase alphanumeric with hyphens."],
                    }
                );
            }

            if (!await store.IsSlugAvailableAsync(slug, orgId, ct))
            {
                return TypedResults.ValidationProblem(
                    new Dictionary<string, string[]> { ["slug"] = ["This slug is already in use."] }
                );
            }

            org.Slug = slug;
        }

        // The settings form submits the full icon state; null means clear.
        org.IconUrl = request.IconUrl;

        await store.UpdateOrganizationAsync(org, ct);

        var creatorEmail = await store.FindUserEmailByIdAsync(org.CreatedByUserId, ct);
        var creatorDomain = EmailDomainNormalizer.FromEmail(creatorEmail);
        var domainAvailable =
            creatorDomain is null
            || await store.IsAutoInviteSameDomainAvailableAsync(creatorDomain, org.Id, ct);
        var sameDomainStatus = OrganizationSameDomainOnboardingStatusFactory.Create(
            org,
            creatorEmail,
            domainAvailable
        );

        return TypedResults.Ok(
            OrganizationSameDomainOnboardingStatusFactory.ToOrganizationResponse(
                org,
                membership.Role,
                sameDomainStatus
            )
        );
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();
}
