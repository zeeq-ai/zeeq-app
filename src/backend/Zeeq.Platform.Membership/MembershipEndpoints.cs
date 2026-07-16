using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Browser-authenticated endpoints for the current user's own memberships:
/// switching active org, leaving orgs, and setting defaults.
/// </summary>
public sealed class MembershipEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("me")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );

        // POST /api/v1/me/switch-org/{orgId}
        group
            .MapPost(
                "/switch-org/{orgId}",
                static async Task<Results<NoContent, NotFound>> (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] SwitchOrgHandler handler,
                    HttpContext httpContext,
                    CancellationToken ct
                ) =>
                {
                    var result = await handler.HandleAsync(orgId, user, ct);

                    if (result is { Result: Ok<ClaimsPrincipal> ok } && ok.Value is { } principal)
                    {
                        await httpContext.SignInAsync(
                            SetupIdentityExtension.CookieScheme,
                            principal
                        );

                        return TypedResults.NoContent();
                    }

                    return TypedResults.NotFound();
                }
            )
            .WithName("SwitchOrganization")
            .WithTags("Memberships")
            .WithSummary("Switch active organization.")
            .WithDescription(
                """
                Switches the authenticated user's active organization to `orgId` and re-issues
                the session cookie with the new org, team, slug, and role claims. Subsequent
                requests are then scoped to the newly active organization.

                Returns `404` if the user is not a member of `orgId`.
                """
            );

        // DELETE /api/v1/me/memberships/{orgId}
        group
            .MapDelete(
                "/memberships/{orgId}",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] LeaveOrgHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("LeaveOrganization")
            .WithTags("Memberships")
            .WithSummary("Leave an organization.")
            .WithDescription(
                """
                Removes the authenticated user's own membership in `orgId`. The sole remaining
                owner cannot leave — ownership must first be transferred to another member —
                so an organization is never left without an owner.
                """
            );

        // PUT /api/v1/me/memberships/{orgId}/default
        group
            .MapPut(
                "/memberships/{orgId}/default",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] SetDefaultOrgHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("SetDefaultOrganization")
            .WithTags("Memberships")
            .WithSummary("Set default organization.")
            .WithDescription(
                """
                Marks `orgId` as the authenticated user's default organization — the one their
                session starts in at sign-in. Does not change the currently active organization
                for this session; use switch-org for that.
                """
            );
    }
}
