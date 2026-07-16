using Zeeq.Core.Models;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Resolves the queue service tier for an organization.
/// </summary>
/// <remarks>
/// The interface lives in the transport-neutral messaging project. Runtime
/// composition supplies a cache-backed implementation that can read the current
/// organization tier without coupling the platform project to a concrete store.
/// </remarks>
public interface ITenantTierResolver
{
    /// <summary>
    /// Resolves the tier for an organization.
    /// </summary>
    /// <param name="organizationId">Organization identifier.</param>
    /// <param name="cancellationToken">Cancellation token for cache or store access.</param>
    /// <returns>Resolved organization tier.</returns>
    ValueTask<OrganizationTier> ResolveTierAsync(
        string organizationId,
        CancellationToken cancellationToken = default
    );
}
