namespace Zeeq.Platform.Messaging;

/// <summary>
/// Identifies the concrete routing lane selected for a Zeeq message.
/// </summary>
/// <remarks>
/// Transport adapters use this value to map the transport-neutral route to a
/// physical resource such as a Postgres queue table or a Pub/Sub topic group.
/// The route string remains the Brighter routing key.
/// </remarks>
public enum MessagingRouteKind
{
    /// <summary>
    /// Tenant-scoped work split by organization tier and stable bucket.
    /// </summary>
    Tenant,

    /// <summary>
    /// Platform-scoped work that is not owned by an organization.
    /// </summary>
    System,

    /// <summary>
    /// Tenant-scoped work that bypasses tier buckets for immediate handling.
    /// </summary>
    Immediate,
}
