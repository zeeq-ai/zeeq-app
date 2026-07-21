using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Updates same-domain onboarding settings for an organization.
/// </summary>
public sealed class UpdateSameDomainOnboardingHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    /// <summary>
    /// Enables or disables same-domain onboarding. Enabling derives the
    /// claimable domain from the organization's creator email at write time.
    /// </summary>
    public async Task<
        Results<Ok<SameDomainOnboardingStatusResponse>, NotFound, ValidationProblem>
    > HandleAsync(
        string orgId,
        UpdateSameDomainOnboardingRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (request.Enabled is not { } enabled)
        {
            return ValidationProblem("enabled", "The enabled field is required.");
        }

        var requestedRole = NormalizeRole(request.DefaultRole);
        if (requestedRole is null)
        {
            return ValidationProblem("defaultRole", "Default role must be either admin or member.");
        }

        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var memberships = await store.ListActiveMembershipsForUserAsync(userId, ct);
        var membership = memberships.FirstOrDefault(m => m.OrganizationId == orgId);
        if (membership is null)
        {
            return TypedResults.NotFound();
        }

        var organization = await store.FindOrganizationByIdAsync(orgId, ct);
        if (organization is null)
        {
            return TypedResults.NotFound();
        }

        var creatorEmail = await store.FindUserEmailByIdAsync(organization.CreatedByUserId, ct);

        if (!enabled)
        {
            organization.AutoInviteSameDomainEnabled = false;
            organization.AutoInviteSameDomain = null;
            organization.AutoInviteDefaultRole =
                OrganizationSameDomainOnboardingStatusFactory.DefaultRole;

            await store.UpdateOrganizationSameDomainOnboardingAsync(organization, ct);

            var disabledStatus = OrganizationSameDomainOnboardingStatusFactory.Create(
                organization,
                creatorEmail,
                domainAvailable: true
            );

            return TypedResults.Ok(disabledStatus);
        }

        var domain = EmailDomainNormalizer.FromEmail(creatorEmail);
        if (domain is null)
        {
            return ValidationProblem(
                "domain",
                "The organization creator must have a valid email domain."
            );
        }

        if (PublicEmailDomainCatalog.IsPublicEmailDomain(domain))
        {
            return ValidationProblem(
                "domain",
                "Same-domain onboarding cannot be enabled for public email domains."
            );
        }

        if (!await store.IsAutoInviteSameDomainAvailableAsync(domain, organization.Id, ct))
        {
            return ValidationProblem(
                "domain",
                "This domain is already used by another organization."
            );
        }

        organization.AutoInviteSameDomainEnabled = true;
        organization.AutoInviteSameDomain = domain;
        organization.AutoInviteDefaultRole = requestedRole;

        if (!await store.UpdateOrganizationSameDomainOnboardingAsync(organization, ct))
        {
            return ValidationProblem(
                "domain",
                "This domain is already used by another organization."
            );
        }

        return TypedResults.Ok(
            new SameDomainOnboardingStatusResponse(
                Enabled: true,
                Domain: domain,
                DefaultRole: requestedRole,
                CanEnable: true,
                BlockReason: null
            )
        );
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return OrganizationSameDomainOnboardingStatusFactory.DefaultRole;
        }

        var normalized = role.Trim().ToLowerInvariant();

        return normalized is "admin" or "member" ? normalized : null;
    }

    private static ValidationProblem ValidationProblem(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
