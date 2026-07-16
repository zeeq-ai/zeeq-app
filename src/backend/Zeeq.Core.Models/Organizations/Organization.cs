namespace Zeeq.Core.Models;

/// <summary>
/// Organization that owns teams, memberships, and content partitions.
/// </summary>
/// <remarks>
/// <para>
/// An organization is the top-level tenant boundary. Every user belongs to
/// at least one organization (created automatically on first login).
/// Organizations contain <see cref="Team"/> records and scope
/// <see cref="Partition"/> records.
/// </para>
/// <para>
/// Disabled organizations must fail closed: all authentication and
/// authorization for members of a disabled org must be rejected.
/// </para>
/// <para>Backed by the <c>core_organizations</c> table.</para>
/// </remarks>
public sealed class Organization : MutableDomainEntityBase
{
    /// <summary>
    /// Human-readable organization name.
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// URL-safe unique slug (lowercase, alphanumeric + hyphens). Used for
    /// org-scoped UI routes like <c>/o/some-org/home</c>.
    /// </summary>
    /// <remarks>
    /// Must be unique across all organizations. Indexed for lookup.
    /// Max 128 characters. Validated as <c>[a-z0-9]+(?:-[a-z0-9]+)*</c>.
    /// </remarks>
    public string? Slug { get; set; }

    /// <summary>
    /// Base64 data URL for the organization icon (PNG, JPG, or JPEG).
    /// Max 64 KB before base64 encoding. <see langword="null"/> means
    /// no icon set.
    /// </summary>
    public string? IconUrl { get; set; }

    /// <summary>
    /// UTC timestamp when the organization became active.
    /// </summary>
    /// <remarks>
    /// An organization is active only when this value is set and
    /// <c>DisabledAtUtc</c> is null.
    /// </remarks>
    public DateTimeOffset? ActivatedAtUtc { get; set; }

    /// <summary>
    /// Queue service tier used for tenant message routing.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="OrganizationTier.Default"/> so new organizations
    /// immediately route through the baseline messaging capacity.
    /// </remarks>
    public OrganizationTier Tier { get; set; } = OrganizationTier.Default;

    /// <summary>
    /// Organization LLM model tier configuration.
    /// </summary>
    /// <remarks>
    /// The payload maps fast/high/max tiers to provider, model id, and encrypted
    /// value id. API handlers own validation before writing this configuration.
    /// </remarks>
    public OrganizationLlmConfiguration LlmConfiguration { get; set; } =
        OrganizationLlmConfiguration.Default;

    /// <summary>
    /// Optional organization-level code review execution configuration.
    /// </summary>
    /// <remarks>
    /// A null value means code review execution should use
    /// <see cref="CodeReviewOrganizationSettings.Default"/>.
    /// </remarks>
    public CodeReviewOrganizationSettings? CodeReviewConfiguration { get; set; }

    /// <summary>
    /// Local user who created this organization.
    /// </summary>
    public required string CreatedByUserId { get; init; }
}
