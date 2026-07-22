using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Generic paged response returned by system organization admin endpoints.
/// </summary>
/// <typeparam name="T">Item type contained in the page.</typeparam>
/// <param name="Items">Items returned for the requested page.</param>
/// <param name="Page">One-based page number that was requested.</param>
/// <param name="PageSize">Maximum number of items requested per page.</param>
/// <param name="TotalCount">Total number of matching items across all pages.</param>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount
);

/// <summary>
/// Creator profile displayed in system organization admin responses.
/// </summary>
/// <param name="UserId">Stable local user ID of the organization creator.</param>
/// <param name="DisplayName">Current display name of the organization creator.</param>
/// <param name="Email">Current primary email address of the creator, when known.</param>
/// <param name="PictureUrl">Current creator avatar URL, when known.</param>
public sealed record SystemOrganizationCreatorResponse(
    string UserId,
    string DisplayName,
    string? Email,
    string? PictureUrl
);

/// <summary>
/// LLM provider/model/key metadata for one organization tier.
/// </summary>
/// <param name="Tier">LLM quality tier label, such as <c>Fast</c>, <c>High</c>, or <c>Max</c>.</param>
/// <param name="Provider">LLM provider configured for this tier.</param>
/// <param name="Model">Provider-specific model identifier configured for this tier.</param>
/// <param name="Endpoint">Optional provider endpoint URL configured for this tier.</param>
/// <param name="UsesManagedKey">Whether this tier uses a tenant-managed encrypted key.</param>
/// <param name="KeyId">Encrypted key metadata ID when <paramref name="UsesManagedKey"/> is true.</param>
public sealed record SystemOrganizationLlmTierResponse(
    string Tier,
    string Provider,
    string Model,
    string? Endpoint,
    bool UsesManagedKey,
    string? KeyId
);

/// <summary>
/// View-only organization LLM configuration for all tiers.
/// </summary>
/// <param name="Fast">Configuration used for the fast LLM quality tier.</param>
/// <param name="High">Configuration used for the high-quality LLM tier.</param>
/// <param name="Max">Configuration used for the maximum-quality LLM tier.</param>
public sealed record SystemOrganizationLlmConfigurationResponse(
    SystemOrganizationLlmTierResponse Fast,
    SystemOrganizationLlmTierResponse High,
    SystemOrganizationLlmTierResponse Max
);

/// <summary>
/// Organization row returned by the system-admin list endpoint.
/// </summary>
/// <param name="Id">Stable organization identifier.</param>
/// <param name="DisplayName">Human-readable organization name.</param>
/// <param name="Slug">URL-safe organization slug, when set.</param>
/// <param name="IconUrl">Organization icon URL or data URL, when set.</param>
/// <param name="Creator">Creator profile associated with the organization.</param>
/// <param name="CreatedAtUtc">UTC timestamp when the organization was created.</param>
/// <param name="UpdatedAtUtc">UTC timestamp when the organization was last updated.</param>
/// <param name="ActivatedAtUtc">UTC timestamp when the organization became active.</param>
/// <param name="DisabledAtUtc">UTC timestamp when the organization was disabled.</param>
/// <param name="MemberCount">Number of active organization members.</param>
/// <param name="Tier">Organization service tier name.</param>
/// <param name="LlmConfiguration">View-only LLM configuration summary for the organization.</param>
public sealed record SystemOrganizationSummaryResponse(
    string Id,
    string DisplayName,
    string? Slug,
    string? IconUrl,
    SystemOrganizationCreatorResponse Creator,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? DisabledAtUtc,
    int MemberCount,
    string Tier,
    SystemOrganizationLlmConfigurationResponse LlmConfiguration
);

/// <summary>
/// Detailed organization response used by the system-admin slideover.
/// </summary>
/// <param name="Id">Stable organization identifier.</param>
/// <param name="DisplayName">Human-readable organization name.</param>
/// <param name="Slug">URL-safe organization slug, when set.</param>
/// <param name="IconUrl">Organization icon URL or data URL, when set.</param>
/// <param name="Creator">Creator profile associated with the organization.</param>
/// <param name="CreatedAtUtc">UTC timestamp when the organization was created.</param>
/// <param name="UpdatedAtUtc">UTC timestamp when the organization was last updated.</param>
/// <param name="ActivatedAtUtc">UTC timestamp when the organization became active.</param>
/// <param name="DisabledAtUtc">UTC timestamp when the organization was disabled.</param>
/// <param name="MemberCount">Number of active organization members.</param>
/// <param name="Tier">Organization service tier name.</param>
/// <param name="LlmConfiguration">View-only LLM configuration summary for the organization.</param>
public sealed record SystemOrganizationDetailsResponse(
    string Id,
    string DisplayName,
    string? Slug,
    string? IconUrl,
    SystemOrganizationCreatorResponse Creator,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? DisabledAtUtc,
    int MemberCount,
    string Tier,
    SystemOrganizationLlmConfigurationResponse LlmConfiguration
);

/// <summary>
/// Active organization member displayed in the system-admin member table.
/// </summary>
/// <param name="UserId">Stable local user ID for the active member.</param>
/// <param name="DisplayName">Current display name from the member profile.</param>
/// <param name="Email">Current primary email address for the member, when known.</param>
/// <param name="PictureUrl">Current member avatar URL, when known.</param>
/// <param name="Role">Organization role assigned to the active member.</param>
/// <param name="JoinedAtUtc">UTC timestamp when the active membership row was created.</param>
public sealed record SystemOrganizationMemberResponse(
    string UserId,
    string DisplayName,
    string? Email,
    string? PictureUrl,
    string Role,
    DateTimeOffset JoinedAtUtc
);

/// <summary>
/// Request body for platform-admin organization activation and tier updates.
/// </summary>
public sealed class UpdateSystemOrganizationRequest
{
    /// <summary>
    /// Desired active state. Omit to leave activation unchanged.
    /// </summary>
    public bool? Active { get; init; }

    /// <summary>
    /// Desired organization tier. Omit to leave tier unchanged.
    /// </summary>
    [MaxLength(32)]
    public string? Tier { get; init; }
}

internal static class SystemOrganizationAdminContractMapping
{
    extension(SystemOrganizationPage<SystemOrganizationSummary> page)
    {
        public PagedResponse<SystemOrganizationSummaryResponse> ToResponse() =>
            new(
                [.. page.Items.Select(organization => organization.ToResponse())],
                page.Page,
                page.PageSize,
                page.TotalCount
            );
    }

    extension(SystemOrganizationPage<SystemOrganizationMember> page)
    {
        public PagedResponse<SystemOrganizationMemberResponse> ToResponse() =>
            new(
                [.. page.Items.Select(member => member.ToResponse())],
                page.Page,
                page.PageSize,
                page.TotalCount
            );
    }

    extension(SystemOrganizationSummary organization)
    {
        public SystemOrganizationSummaryResponse ToResponse() =>
            new(
                organization.Id,
                organization.DisplayName,
                organization.Slug,
                organization.IconUrl,
                organization.Creator.ToResponse(),
                organization.CreatedAtUtc,
                organization.UpdatedAtUtc,
                organization.ActivatedAtUtc,
                organization.DisabledAtUtc,
                organization.MemberCount,
                organization.Tier.ToString(),
                organization.LlmConfiguration.ToResponse()
            );
    }

    extension(SystemOrganizationDetails organization)
    {
        public SystemOrganizationDetailsResponse ToResponse() =>
            new(
                organization.Id,
                organization.DisplayName,
                organization.Slug,
                organization.IconUrl,
                organization.Creator.ToResponse(),
                organization.CreatedAtUtc,
                organization.UpdatedAtUtc,
                organization.ActivatedAtUtc,
                organization.DisabledAtUtc,
                organization.MemberCount,
                organization.Tier.ToString(),
                organization.LlmConfiguration.ToResponse()
            );
    }

    extension(SystemOrganizationMember member)
    {
        private SystemOrganizationMemberResponse ToResponse() =>
            new(
                member.UserId,
                member.DisplayName,
                member.Email,
                member.PictureUrl,
                member.Role,
                member.JoinedAtUtc
            );
    }

    extension(SystemOrganizationCreator creator)
    {
        private SystemOrganizationCreatorResponse ToResponse() =>
            new(creator.UserId, creator.DisplayName, creator.Email, creator.PictureUrl);
    }

    extension(SystemOrganizationLlmConfiguration configuration)
    {
        private SystemOrganizationLlmConfigurationResponse ToResponse() =>
            new(
                configuration.Fast.ToResponse(),
                configuration.High.ToResponse(),
                configuration.Max.ToResponse()
            );
    }

    extension(SystemOrganizationLlmTier tier)
    {
        private SystemOrganizationLlmTierResponse ToResponse() =>
            new(
                tier.Tier,
                tier.Provider,
                tier.Model,
                tier.Endpoint,
                tier.UsesManagedKey,
                tier.KeyId
            );
    }
}
