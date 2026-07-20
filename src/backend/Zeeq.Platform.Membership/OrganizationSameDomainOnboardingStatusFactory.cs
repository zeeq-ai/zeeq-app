using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership;

internal static class OrganizationSameDomainOnboardingStatusFactory
{
    public const string DefaultRole = "member";

    public static SameDomainOnboardingStatusResponse Create(
        Organization organization,
        string? creatorEmail,
        bool domainAvailable
    )
    {
        var defaultRole = string.IsNullOrWhiteSpace(organization.AutoInviteDefaultRole)
            ? DefaultRole
            : organization.AutoInviteDefaultRole;
        var domain = EmailDomainNormalizer.FromEmail(creatorEmail);

        if (organization.AutoInviteSameDomainEnabled)
        {
            return new SameDomainOnboardingStatusResponse(
                Enabled: true,
                Domain: organization.AutoInviteSameDomain,
                DefaultRole: defaultRole,
                CanEnable: true,
                BlockReason: null
            );
        }

        if (domain is null)
        {
            return Disabled(defaultRole, "creator_email_unavailable");
        }

        if (PublicEmailDomainCatalog.IsPublicEmailDomain(domain))
        {
            return Disabled(defaultRole, "public_email_domain");
        }

        if (!domainAvailable)
        {
            return Disabled(defaultRole, "domain_already_claimed");
        }

        return new SameDomainOnboardingStatusResponse(
            Enabled: false,
            Domain: domain,
            DefaultRole: defaultRole,
            CanEnable: true,
            BlockReason: null
        );
    }

    public static OrganizationResponse ToOrganizationResponse(
        Organization organization,
        string? role,
        SameDomainOnboardingStatusResponse status
    ) =>
        new(
            Id: organization.Id,
            Slug: organization.Slug,
            DisplayName: organization.DisplayName,
            IconUrl: organization.IconUrl,
            Role: role,
            CreatedAtUtc: organization.CreatedAtUtc,
            ActivatedAtUtc: organization.ActivatedAtUtc,
            AutoInviteSameDomainEnabled: status.Enabled,
            AutoInviteSameDomain: status.Domain,
            AutoInviteDefaultRole: status.DefaultRole,
            AutoInviteSameDomainCanEnable: status.CanEnable,
            AutoInviteSameDomainBlockReason: status.BlockReason
        );

    private static SameDomainOnboardingStatusResponse Disabled(
        string defaultRole,
        string blockReason
    ) =>
        new(
            Enabled: false,
            Domain: null,
            DefaultRole: defaultRole,
            CanEnable: false,
            BlockReason: blockReason
        );
}
