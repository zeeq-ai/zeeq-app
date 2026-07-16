using System.Security.Claims;

namespace Zeeq.Core.Identity;

/// <summary>
/// Reconstructs persisted ownership context from an authenticated cookie or token principal.
/// </summary>
/// <remarks>
/// Management endpoints use this helper before creating client credentials, user tokens,
/// or other local auth artifacts. Ownership must come from server-issued claims created
/// after the external IdP login, never from request body parameters supplied by the caller.
/// </remarks>
internal static class AuthenticatedOwnerContext
{
    /// <summary>
    /// Reads the local owner, tenant, partition, and upstream IdP claims required for persistence.
    /// </summary>
    /// <param name="user">Authenticated principal from the app cookie or OpenIddict validation.</param>
    /// <returns>Owner context suitable for storing on local auth metadata rows.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a required server-issued identity claim is missing.
    /// </exception>
    public static OwnerContext FromClaimsPrincipal(ClaimsPrincipal user)
    {
        var identity = user.AsZeeqIdentity();

        if (string.IsNullOrEmpty(identity.OwnerUserId))
        {
            throw new InvalidOperationException("The authenticated user has no subject claim.");
        }

        if (string.IsNullOrEmpty(identity.OrganizationId))
        {
            throw new InvalidOperationException(
                "The authenticated user has no organization claim."
            );
        }

        if (string.IsNullOrEmpty(identity.TeamId))
        {
            throw new InvalidOperationException("The authenticated user has no team claim.");
        }

        if (string.IsNullOrEmpty(identity.Provider))
        {
            throw new InvalidOperationException("The authenticated user has no provider claim.");
        }

        if (string.IsNullOrEmpty(identity.ProviderSubject))
        {
            throw new InvalidOperationException(
                "The authenticated user has no provider subject claim."
            );
        }

        return new OwnerContext(
            identity.OwnerUserId,
            identity.OrganizationId,
            identity.TeamId,
            identity.PartitionIdsJson ?? "[]",
            identity.Provider,
            identity.ProviderSubject
        );
    }
}
