using System.ComponentModel.DataAnnotations;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Result of checking whether an organization slug can be used.
/// </summary>
/// <param name="Slug">Slug value that was checked.</param>
/// <param name="Available">Whether the slug is available.</param>
public sealed record SlugCheckResponse(string Slug, bool Available);

// NOTE: Slug is intentionally retained on create requests even though the
// server currently generates it. The UI hides slug editing today, but product
// may add a caller-provided create slug later.
/// <summary>
/// Request body for creating an organization.
/// </summary>
/// <param name="DisplayName">Human-readable organization name.</param>
/// <param name="Slug">Optional caller-provided slug.</param>
/// <param name="IconUrl">Optional organization icon data URL.</param>
public sealed record CreateOrganizationRequest(
    [property: Required, MaxLength(200)] string DisplayName,
    [property: MaxLength(128), RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")] string? Slug,
    [property: MaxLength(87_380)] string? IconUrl
);

/// <summary>
/// Request body for updating organization display details.
/// </summary>
/// <param name="DisplayName">Optional replacement display name.</param>
/// <param name="Slug">Optional replacement slug.</param>
/// <param name="IconUrl">Optional replacement icon data URL.</param>
public sealed record UpdateOrganizationRequest(
    [property: MaxLength(200)] string? DisplayName,
    [property: MaxLength(128), RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")] string? Slug,
    [property: MaxLength(87_380)] string? IconUrl
);

/// <summary>
/// Organization details returned by membership endpoints.
/// </summary>
/// <param name="Id">Stable organization identifier.</param>
/// <param name="Slug">Organization slug.</param>
/// <param name="DisplayName">Human-readable organization name.</param>
/// <param name="IconUrl">Optional organization icon data URL.</param>
/// <param name="Role">Current user's role in the organization, when available.</param>
/// <param name="CreatedAtUtc">UTC timestamp when the organization was created.</param>
/// <param name="ActivatedAtUtc">UTC timestamp when the organization became active.</param>
/// <param name="AutoInviteSameDomainEnabled">Whether same-domain onboarding is enabled.</param>
/// <param name="AutoInviteSameDomain">Normalized domain currently claimed for auto-invites.</param>
/// <param name="AutoInviteDefaultRole">Role assigned to auto-created same-domain invitations.</param>
/// <param name="AutoInviteSameDomainCanEnable">Whether the organization can enable same-domain onboarding.</param>
/// <param name="AutoInviteSameDomainBlockReason">Reason enabling is blocked, when any.</param>
public sealed record OrganizationResponse(
    string Id,
    string? Slug,
    string DisplayName,
    string? IconUrl,
    string? Role,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    bool AutoInviteSameDomainEnabled,
    string? AutoInviteSameDomain,
    string AutoInviteDefaultRole,
    bool AutoInviteSameDomainCanEnable,
    string? AutoInviteSameDomainBlockReason
);

/// <summary>
/// Request body for same-domain onboarding settings.
/// </summary>
public sealed class UpdateSameDomainOnboardingRequest
{
    /// <summary>
    /// Whether same-domain onboarding should be enabled.
    /// </summary>
    [Required]
    public bool? Enabled { get; init; }

    /// <summary>
    /// Role assigned to auto-created invitations; defaults to <c>member</c>.
    /// </summary>
    [MaxLength(64)]
    [RegularExpression("^(admin|member)$")]
    public string? DefaultRole { get; init; }
}

/// <summary>
/// Current same-domain onboarding settings and enablement eligibility.
/// </summary>
/// <param name="Enabled">Whether same-domain onboarding is enabled.</param>
/// <param name="Domain">Normalized domain currently claimed for auto-invites.</param>
/// <param name="DefaultRole">Role assigned to auto-created invitations.</param>
/// <param name="CanEnable">Whether the organization can enable same-domain onboarding.</param>
/// <param name="BlockReason">Reason enabling is blocked, when any.</param>
public sealed record SameDomainOnboardingStatusResponse(
    bool Enabled,
    string? Domain,
    string DefaultRole,
    bool CanEnable,
    string? BlockReason
);
