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
/// <para>
/// This middleware enriches the role claim only; it deliberately never
/// rejects a request based on membership status. It stays permissive
/// because it runs globally for <b>all</b> authenticated traffic (cookie/
/// session and bearer), including recovery-path endpoints that must remain
/// reachable even when the caller's current org membership is inactive or
/// missing: <c>/me</c> (<c>MeEndpoints.Handler.GetMe.cs</c>, which returns
/// pending invitations for users without an active current membership),
/// switch-org (<c>MembershipEndpoints.Handler.SwitchOrg.cs</c>, the escape
/// hatch for a stale org claim), and accept-invitation.
/// </para>
/// <para>
/// <see cref="UserTokenValidationMiddleware"/>, which runs immediately
/// before this one in the pipeline (<c>Program.cs</c>), is where
/// membership-based rejection <i>does</i> happen — scoped to bearer-token
/// requests only. Bearer tokens have no interactive "let the user through
/// so they can fix their own access" case the way a cookie session does, so
/// failing fast there is correct; doing the same here would 401/403 users
/// before they reach the very endpoints designed to get them unstuck.
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
