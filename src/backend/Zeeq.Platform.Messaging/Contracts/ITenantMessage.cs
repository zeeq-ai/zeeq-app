namespace Zeeq.Platform.Messaging;

/// <summary>
/// Marker contract for messages scoped to an organization and optional team.
/// </summary>
/// <remarks>
/// Tenant messages route through the tenant-tier resolver and stable bucket
/// router. Implementers must provide a stable organization identifier because
/// it is part of the distribution key for fair queue capacity.
/// </remarks>
public interface ITenantMessage
{
    /// <summary>
    /// Organization that owns the queued work.
    /// </summary>
    string OrganizationId { get; }

    /// <summary>
    /// Optional team that scopes the queued work inside the organization.
    /// </summary>
    string? TeamId { get; }
}
