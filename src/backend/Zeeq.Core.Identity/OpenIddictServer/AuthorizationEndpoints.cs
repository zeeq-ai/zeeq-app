using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Pass-through OpenIddict authorization endpoint.
/// </summary>
/// <remarks>
/// This endpoint is where a browser cookie created from an external IdP login
/// is converted into an OpenIddict authorization code for an MCP client. It also
/// claims pending DCR setup rows before code issuance so unowned public clients
/// cannot proceed to token exchange.
/// </remarks>
public static class AuthorizationEndpoints
{
    /// <summary>
    /// Maps GET and POST authorization endpoint passthrough routes used by OpenIddict.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connect/authorize", AuthorizeAsync).ExcludeFromDescription().AllowAnonymous();
        app.MapPost("/connect/authorize", AuthorizeAsync).ExcludeFromDescription().AllowAnonymous();

        return app;
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext httpContext,
        AuthSettings settings,
        DcrClientSetupService setupService,
        IZeeqMembershipStore membershipStore,
        CancellationToken cancellationToken
    )
    {
        var request =
            httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException(
                "The OpenIddict authorization request is missing."
            );

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            // OpenIddict owns the protocol request, but the human identity comes
            // from our external-login cookie flow. Preserve the full authorize URL
            // so login can resume the original OAuth request afterward.
            // Use an absolute URL so NormalizeReturnUrl treats it as a trusted
            // same-origin redirect rather than routing it through the frontend base.
            var returnUrl =
                settings.IssuerTrimmed + httpContext.Request.Path + httpContext.Request.QueryString;

            return Results.Redirect(
                $"{settings.FrontendBaseUriTrimmed}/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}"
            );
        }

        // A user who belongs to more than one organization must choose which org
        // the DCR client should be scoped to before we bind the setup. Single-org
        // users fall straight through to the existing claim + code-issuance path.
        // The picker is a Vue route on the same origin as this endpoint (the
        // interactive-auth origin), so the identity cookie travels with the
        // redirect and the picker can call /me + switch-org to bind the choice.
        var userId = httpContext.User.FindFirstValue(Claims.Subject);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var memberships = await membershipStore.ListActiveMembershipsForUserAsync(
                userId,
                cancellationToken
            );

            if (memberships.Count > 1)
            {
                // NOTE: Guard against the infinite redirect loop: after the /select-org picker
                // calls switch-org and navigates back, this endpoint would see memberships.Count > 1
                // again and redirect indefinitely. The picker appends &orgId=<chosen> to the return
                // URL; we skip the redirect only when that param matches the re-issued cookie, which
                // proves switch-org actually ran for that org.
                var activeOrgId = httpContext.User.FindFirstValue(AuthClaims.OrganizationId);
                var confirmedOrgId = httpContext.Request.Query["orgId"].FirstOrDefault();

                if (string.IsNullOrWhiteSpace(confirmedOrgId) || confirmedOrgId != activeOrgId)
                {
                    // Relative on purpose: the picker only accepts same-origin
                    // /connect/authorize return URLs and rejects absolute ones.
                    var authorizeReturnUrl =
                        httpContext.Request.Path + httpContext.Request.QueryString;

                    return Results.Redirect(
                        $"{settings.FrontendBaseUriTrimmed}/select-org?returnUrl={Uri.EscapeDataString(authorizeReturnUrl)}"
                    );
                }
            }
        }

        var setupDecision = await setupService.ClaimOrValidateActiveAsync(
            request.ClientId,
            httpContext.User,
            cancellationToken
        );
        if (!setupDecision.Succeeded)
        {
            return ForbidWithOpenIddictError(setupDecision);
        }

        // Build a fresh OpenIddict principal instead of reusing the cookie
        // principal directly. This lets us choose claim destinations and keeps
        // browser-only details out of access tokens.
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role
        );

        CopyClaim(httpContext.User, identity, Claims.Subject);
        CopyClaim(httpContext.User, identity, Claims.Name);
        CopyClaim(httpContext.User, identity, Claims.Email);
        CopyClaim(httpContext.User, identity, AuthClaims.OrganizationId);
        CopyClaim(httpContext.User, identity, AuthClaims.TeamId);
        CopyClaim(httpContext.User, identity, AuthClaims.PartitionIds);
        CopyClaim(httpContext.User, identity, AuthClaims.Provider);
        CopyClaim(httpContext.User, identity, AuthClaims.ProviderSubject);

        identity.SetDestinations(claim =>
            claim.Type switch
            {
                Claims.Subject or Claims.Name or Claims.Email =>
                [
                    Destinations.AccessToken,
                    Destinations.IdentityToken,
                ],
                AuthClaims.OrganizationId
                or AuthClaims.TeamId
                or AuthClaims.PartitionIds
                or AuthClaims.Provider
                or AuthClaims.ProviderSubject => [Destinations.AccessToken],
                _ => [Destinations.AccessToken],
            }
        );

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources(settings.ResourceTrimmed);

        return Results.SignIn(
            principal,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme
        );
    }

    private static IResult ForbidWithOpenIddictError(DcrSetupDecision decision)
    {
        var properties = new AuthenticationProperties(
            new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                    decision.Error ?? Errors.InvalidClient,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                    decision.ErrorDescription,
            }
        );

        return Results.Forbid(
            properties,
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]
        );
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
