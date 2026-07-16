using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity;

/// <summary>
/// Adds the current org's role (<c>owner</c> / <c>admin</c> / <c>member</c>)
/// to the principal's configured role claim on every authenticated request, so that
/// <c>[RequireRole]</c> and <c>.RequireAuthorization(b => b.RequireRole(...))</c>
/// work with standard ASP.NET auth policies.
/// </summary>
/// <remarks>
/// <para>
/// Runs after <c>UseAuthentication()</c> (which populates <c>HttpContext.User</c>
/// from the cookie or JWT) and before <c>UseAuthorization()</c> (which evaluates
/// policies). Roles are resolved fresh from <see cref="IZeeqMembershipStore"/>
/// on every request — never cached in the token or cookie — so that role changes
/// take effect immediately.
/// </para>
/// <para>
/// Follows the same inline-middleware pattern as
/// <see cref="UserTokenValidationMiddleware"/>.
/// </para>
/// </remarks>
public static class MembershipEnrichmentMiddleware
{
    /// <summary>
    /// Adds middleware that injects the current organization role into the request principal.
    /// </summary>
    public static IApplicationBuilder UseMembershipEnrichment(this IApplicationBuilder app) =>
        app.Use(
            async (context, next) =>
            {
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    var userId = context.User.FindFirstValue(OpenIddictConstants.Claims.Subject);
                    var orgId = context.User.FindFirstValue(AuthClaims.OrganizationId);

                    if (userId is not null && orgId is not null)
                    {
                        var store =
                            context.RequestServices.GetRequiredService<IZeeqMembershipStore>();

                        var memberships = await store.ListActiveMembershipsForUserAsync(
                            userId,
                            context.RequestAborted
                        );

                        var role = memberships.FirstOrDefault(m => m.OrganizationId == orgId)?.Role;

                        if (role is not null)
                        {
                            var identity = (ClaimsIdentity)context.User.Identity!;
                            var roleClaimType = identity.RoleClaimType;

                            // Remove any previously injected org-role claims to keep the set idempotent.
                            foreach (var claim in identity.FindAll(roleClaimType).ToArray())
                            {
                                identity.RemoveClaim(claim);
                            }

                            identity.AddClaim(new Claim(roleClaimType, role));
                        }
                    }
                }

                await next(context);
            }
        );
}
