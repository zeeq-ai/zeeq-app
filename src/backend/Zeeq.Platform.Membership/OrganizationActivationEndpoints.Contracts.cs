using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Authenticated activation-key exchange request.
/// </summary>
public sealed record OrganizationActivationExchangeRequest(
    [property: Required, StringLength(64, MinimumLength = 64)] string Key
);

/// <summary>
/// Successful activation-key exchange response.
/// </summary>
public sealed record OrganizationActivationExchangeResponse(string OrganizationId);

/// <summary>
/// System-admin create activation-key request.
/// </summary>
public sealed class CreateSystemActivationKeyRequest
{
    /// <summary>
    /// Optional operator note.
    /// </summary>
    [MaxLength(500)]
    public string? Note { get; init; }

    /// <summary>
    /// Optional key lifetime in days.
    /// </summary>
    [Range(1, PlatformSettings.MaxSupportedOrganizationActivationKeyLifetimeDays)]
    public int? ExpiresInDays { get; init; }
}

/// <summary>
/// System-admin response that returns the raw key only at creation time.
/// </summary>
public sealed record CreateSystemActivationKeyResponse(
    string Id,
    string Key,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc
);

/// <summary>
/// System-admin activation-key summary.
/// </summary>
public sealed record SystemActivationKeyResponse(
    string Id,
    string? Note,
    string CreatedByUserId,
    string CreatedByDisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    string? ActivatedOrganizationId,
    string? ActivatedByUserId,
    DateTimeOffset? DisabledAtUtc,
    OrganizationActivationKeyStatus Status
);

internal static class OrganizationActivationContractMapping
{
    extension(OrganizationActivationKeyPage<OrganizationActivationKeySummary> page)
    {
        public PagedResponse<SystemActivationKeyResponse> ToResponse() =>
            new(
                [.. page.Items.Select(key => key.ToResponse())],
                page.Page,
                page.PageSize,
                page.TotalCount
            );
    }

    extension(OrganizationActivationKeySummary key)
    {
        public SystemActivationKeyResponse ToResponse() =>
            new(
                key.Id,
                key.Note,
                key.CreatedByUserId,
                key.CreatedByDisplayName,
                key.CreatedAtUtc,
                key.UpdatedAtUtc,
                key.ExpiresAtUtc,
                key.ActivatedAtUtc,
                key.ActivatedOrganizationId,
                key.ActivatedByUserId,
                key.DisabledAtUtc,
                key.Status
            );
    }
}
