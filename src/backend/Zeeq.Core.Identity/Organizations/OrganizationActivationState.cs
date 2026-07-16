namespace Zeeq.Core.Identity;

/// <summary>
/// Narrow organization activation projection used by endpoint filters.
/// </summary>
/// <param name="OrganizationId">Organization identifier.</param>
/// <param name="ActivatedAtUtc">UTC timestamp when the organization became active.</param>
/// <param name="DisabledAtUtc">UTC timestamp when the organization was disabled.</param>
/// <remarks>
/// Filters cache this read model instead of caching EF entities, keeping the cached value
/// immutable and independent of the concrete storage provider.
/// </remarks>
public sealed record OrganizationActivationState(
    string OrganizationId,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? DisabledAtUtc
)
{
    /// <summary>
    /// Whether the organization can service protected API requests.
    /// </summary>
    public bool IsActive => ActivatedAtUtc is not null && DisabledAtUtc is null;
}
