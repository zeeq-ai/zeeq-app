using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// System-admin persistence boundary for platform-wide organization management.
/// </summary>
/// <remarks>
/// This store is intentionally separate from <see cref="IZeeqMembershipStore"/>
/// so tenant-scoped membership operations do not grow platform-admin behavior.
/// Implementations should not apply active-organization or current-tenant
/// filters; system-admin authorization is applied by the admin route group.
/// </remarks>
public interface ISystemOrganizationManagementStore
{
    /// <summary>
    /// Lists all organizations for the system-admin table.
    /// </summary>
    Task<SystemOrganizationPage<SystemOrganizationSummary>> ListOrganizationsAsync(
        int page,
        int pageSize,
        string? query,
        CancellationToken ct
    );

    /// <summary>
    /// Finds one organization with creator and LLM details.
    /// </summary>
    Task<SystemOrganizationDetails?> FindOrganizationAsync(string orgId, CancellationToken ct);

    /// <summary>
    /// Lists active members for a single organization.
    /// </summary>
    Task<SystemOrganizationPage<SystemOrganizationMember>> ListMembersAsync(
        string orgId,
        int page,
        int pageSize,
        CancellationToken ct
    );

    /// <summary>
    /// Applies system-admin activation and tier updates, then returns fresh details.
    /// </summary>
    Task<SystemOrganizationDetails?> UpdateOrganizationAdminStateAsync(
        string orgId,
        bool? active,
        OrganizationTier? tier,
        CancellationToken ct
    );
}

/// <summary>
/// Generic paged result used by the system organization admin store.
/// </summary>
public sealed record SystemOrganizationPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount
);

/// <summary>
/// Creator profile displayed in system organization admin views.
/// </summary>
public sealed record SystemOrganizationCreator(
    string UserId,
    string DisplayName,
    string? Email,
    string? PictureUrl
);

/// <summary>
/// View-only LLM tier configuration for system organization administration.
/// </summary>
public sealed record SystemOrganizationLlmTier(
    string Tier,
    string Provider,
    string Model,
    string? Endpoint,
    bool UsesManagedKey,
    string? KeyId
);

/// <summary>
/// View-only LLM configuration for all organization quality tiers.
/// </summary>
public sealed record SystemOrganizationLlmConfiguration(
    SystemOrganizationLlmTier Fast,
    SystemOrganizationLlmTier High,
    SystemOrganizationLlmTier Max
);

/// <summary>
/// Organization summary row for the system-admin table.
/// </summary>
public sealed record SystemOrganizationSummary(
    string Id,
    string DisplayName,
    string? Slug,
    string? IconUrl,
    SystemOrganizationCreator Creator,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? DisabledAtUtc,
    int MemberCount,
    OrganizationTier Tier,
    SystemOrganizationLlmConfiguration LlmConfiguration
);

/// <summary>
/// Organization detail record for system-admin inspection and edits.
/// </summary>
/// <remarks>
/// NOTE: This intentionally remains separate from <see cref="SystemOrganizationSummary"/>
/// even though the first version has the same shape. The detail surface is expected
/// to grow independently as future admin-only tabs become editable.
/// </remarks>
public sealed record SystemOrganizationDetails(
    string Id,
    string DisplayName,
    string? Slug,
    string? IconUrl,
    SystemOrganizationCreator Creator,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? DisabledAtUtc,
    int MemberCount,
    OrganizationTier Tier,
    SystemOrganizationLlmConfiguration LlmConfiguration
);

/// <summary>
/// Active member row displayed in the system organization details drawer.
/// </summary>
public sealed record SystemOrganizationMember(
    string UserId,
    string DisplayName,
    string? Email,
    string? PictureUrl,
    string Role,
    DateTimeOffset JoinedAtUtc
);
