using System.Security.Claims;
using OpenIddict.Abstractions;
using Zeeq.Core.Documents;

namespace Zeeq.Core.Identity;

/// <summary>Builds the library audience from server-issued claims for the active organization.</summary>
public static class LibraryAudienceExtensions
{
    /// <summary>Returns the caller audience when its active organization matches the requested scope.</summary>
    public static LibraryAudience? GetLibraryAudience(
        this ClaimsPrincipal? principal,
        string? organizationId = null
    )
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var identity = principal.AsZeeqMinimalIdentity();
        if (
            string.IsNullOrWhiteSpace(identity.OwnerUserId)
            || string.IsNullOrWhiteSpace(identity.OrganizationId)
            || organizationId is not null && organizationId != identity.OrganizationId
        )
            return null;

        // Machine credentials have their own subject rather than their creator's memberships.
        if (
            principal.FindFirstValue(AuthClaims.AuthMode)
            == OpenIddictConstants.GrantTypes.ClientCredentials
        )
            return new LibraryAudience(identity.OrganizationId, identity.OwnerUserId, false);

        var administrator = principal.IsInRole("owner") || principal.IsInRole("admin");
        return administrator || principal.IsInRole("member")
            ? new LibraryAudience(identity.OrganizationId, identity.OwnerUserId, administrator)
            : null;
    }
}
