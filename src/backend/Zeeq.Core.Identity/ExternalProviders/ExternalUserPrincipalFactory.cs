using System.Security.Claims;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity;

/// <summary>
/// Builds local app principals from an already verified external
/// IdP identity.
/// </summary>
/// <remarks>
/// OpenIddict is not the human identity provider. The upstream IdP authenticates
/// the user first, then this factory creates the local cookie principal that
/// OpenIddict authorization, browser APIs, and management endpoints reuse.
/// </remarks>
public static class ExternalUserPrincipalFactory
{
    /// <summary>
    /// Creates the browser cookie principal for the resolved local user and tenant context.
    /// </summary>
    /// <remarks>
    /// The local subject is <see cref="AuthContext.UserId"/>. Provider and
    /// provider-subject claims remain as audit metadata for tracing the upstream
    /// identity and must not replace local authorization checks.
    /// </remarks>
    public static ClaimsPrincipal CreateCookiePrincipal(
        AuthContext authContext,
        string provider,
        string providerSubject,
        string? name,
        string? email,
        string? pictureUrl,
        string? orgSlug,
        string? orgRole = null,
        bool isSystemAdmin = false
    )
    {
        var claims = new List<Claim>
        {
            // ── Core identity — who this user is ───────────────────
            new(OpenIddictConstants.Claims.Subject, authContext.UserId),
            new(ClaimTypes.NameIdentifier, authContext.UserId),
            // ── Tenant context — which org/team this session belongs to
            new(AuthClaims.OrganizationId, authContext.OrganizationId),
            new(AuthClaims.TeamId, authContext.TeamId),
            new(AuthClaims.PartitionIds, "[]"),
            // ── Upstream IdP audit — trace the external login source
            new(AuthClaims.Provider, provider),
            new(AuthClaims.ProviderSubject, providerSubject),
        };

        // ── Profile — synced from the external IdP on each login
        if (!string.IsNullOrWhiteSpace(name))
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Name, name));
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Email, email));
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (!string.IsNullOrWhiteSpace(pictureUrl))
        {
            claims.Add(new Claim(AuthClaims.Picture, pictureUrl));
        }

        // ── Routing — org slug for UI org-scoped navigation
        if (!string.IsNullOrWhiteSpace(orgSlug))
        {
            claims.Add(new Claim(AuthClaims.OrganizationSlug, orgSlug));
        }

        // ── Authorization — resolved role for the current org
        if (!string.IsNullOrWhiteSpace(orgRole))
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Role, orgRole));
        }

        // The cookie role is a compatibility signal only. Admin authorization
        // re-evaluates the live configured provider:subject allow-list per request.
        if (isSystemAdmin)
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Role, SystemRoles.SystemAdmin));
        }

        var identity = new ClaimsIdentity(
            claims,
            authenticationType: "ExternalIdpCookie",
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role
        );

        return new ClaimsPrincipal(identity);
    }
}
