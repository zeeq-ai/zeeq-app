using System.Security.Claims;
using Zeeq.Core.Models;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Builds OpenIddict principals for user-owned long-lived bearer tokens.
/// </summary>
/// <remarks>
/// Long-lived user tokens are still OpenIddict access tokens. This factory only
/// chooses the claims, scopes, resource, and per-principal lifetime that
/// OpenIddict will serialize as encrypted JWE by default or signed JWS when
/// configured for compatibility/debugging.
/// </remarks>
public static class UserTokenOpenIddictFactory
{
    /// <summary>
    /// Creates the principal OpenIddict serializes into the long-lived bearer token.
    /// </summary>
    public static ClaimsPrincipal CreatePrincipal(
        ClaimsPrincipal owner,
        UserToken token,
        AuthSettings settings
    )
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role
        );

        CopyClaim(owner, identity, Claims.Name);
        CopyClaim(owner, identity, Claims.Email);

        identity.SetClaim(Claims.Subject, token.OwnerUserId);
        identity.SetClaim(AuthClaims.OrganizationId, token.OrganizationId);
        identity.SetClaim(AuthClaims.TeamId, token.TeamId);
        identity.SetClaim(AuthClaims.PartitionIds, token.SelectedPartitionIdsJson);
        identity.SetClaim(AuthClaims.Provider, token.OwnerProvider);
        identity.SetClaim(AuthClaims.ProviderSubject, token.OwnerProviderSubject);
        identity.SetClaim(AuthClaims.AuthMode, "long_lived_token");
        identity.SetClaim(AuthClaims.UserTokenId, token.Id);
        identity.SetClaim(AuthClaims.UserTokenName, token.DisplayName);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes("mcp:tools");
        principal.SetResources(settings.ResourceTrimmed);
        // The metadata expiry is mirrored into the OpenIddict access-token lifetime.
        // Middleware still checks the row on use so revocation works for self-contained tokens.
        principal.SetAccessTokenLifetime(token.ExpiresAtUtc - token.CreatedAtUtc);

        return principal;
    }

    private static void CopyClaim(ClaimsPrincipal source, ClaimsIdentity target, string type)
    {
        var value = source.FindFirstValue(type);
        if (!string.IsNullOrWhiteSpace(value))
        {
            target.SetClaim(type, value);
        }
    }
}
