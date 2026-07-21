using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Endpoints for organization invitations — scoped under
/// <c>/orgs/{orgId}</c>.
/// </summary>
public static class InvitationEndpointsExtensions
{
    /// <summary>
    /// Maps organization-scoped invitation endpoints.
    /// </summary>
    public static RouteGroupBuilder MapInvitationEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/v1/orgs/{orgId}/invitations
        group
            .MapGet(
                "/invitations",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListOrganizationInvitationsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .WithName("ListOrganizationInvitations")
            .WithTags("Invitations")
            .WithSummary("List sent invitations.")
            .WithDescription(
                """
                Returns the still-pending invitations that have been sent for the organization
                (`orgId`) — invitees who have not yet accepted or declined.

                Requires the `owner` or `admin` role in the organization.
                """
            );

        group
            .MapPost(
                "/invitations",
                static (
                    string orgId,
                    CreateInvitationRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateInvitationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .RequireActiveOrganization()
            .WithName("CreateInvitation")
            .WithTags("Invitations")
            .WithSummary("Invite a user.")
            .WithDescription(
                """
                Creates a pending invitation to the organization (`orgId`) for the email in the
                request, at the requested role. The invitee sees it under their own
                `me/invitations` and joins once they accept.

                Requires the `owner` or `admin` role in the organization.
                """
            );

        group
            .MapDelete(
                "/invitations/{id}",
                static (
                    string orgId,
                    string id,
                    ClaimsPrincipal user,
                    [FromServices] CancelInvitationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, id, user, ct)
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .RequireActiveOrganization()
            .WithName("CancelInvitation")
            .WithTags("Invitations")
            .WithSummary("Cancel an invitation.")
            .WithDescription(
                """
                Withdraws a pending invitation (`id`) previously sent for the organization
                (`orgId`), before the invitee has acted on it. The invitation disappears from
                the invitee's pending list.

                Requires the `owner` or `admin` role in the organization.
                """
            );

        return group;
    }
}

/// <summary>
/// Endpoints for the current user's own invitations.
/// </summary>
public sealed class InvitationEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("me/invitations")
            .RequireAuthorization(
                new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
                {
                    AuthenticationSchemes = Core.Identity.SetupIdentityExtension.CookieScheme,
                }
            );

        // GET /api/v1/me/invitations
        group
            .MapGet(
                "/",
                static (
                    ClaimsPrincipal user,
                    [FromServices] ListInvitationsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(user, ct)
            )
            .WithName("ListInvitations")
            .WithTags("Invitations")
            .WithSummary("List my invitations.")
            .WithDescription(
                """
                Returns the pending organization invitations addressed to the authenticated
                user's email — the invites they can accept or decline. Resolved by email, so
                invitations created before the user signed up still appear once they do.
                """
            );

        // GET /api/v1/me/invitations/{id}/same-domain-details
        group
            .MapGet(
                "/{id}/same-domain-details",
                static (
                    string id,
                    ClaimsPrincipal user,
                    [FromServices] GetSameDomainInvitationDetailsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(id, user, ct)
            )
            .WithName("GetSameDomainInvitationDetails")
            .WithTags("Invitations")
            .WithSummary("Get same-domain invitation details.")
            .WithDescription(
                """
                Returns organization and owner display details for a same-domain invitation
                addressed to the authenticated user.
                """
            );

        // POST /api/v1/me/invitations/{id}/accept
        group
            .MapPost(
                "/{id}/accept",
                static (
                    string id,
                    ClaimsPrincipal user,
                    [FromServices] AcceptInvitationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(id, user, ct)
            )
            .WithName("AcceptInvitation")
            .WithTags("Invitations")
            .WithSummary("Accept an invitation.")
            .WithDescription(
                """
                Accepts the pending invitation (`id`) addressed to the authenticated user,
                creating their membership in the inviting organization at the invited role.

                The user can only act on invitations addressed to their own email. This route
                intentionally remains outside current-org activation filtering so invited
                users can join an active org even when their current org is inactive or unset.
                """
            );

        // POST /api/v1/me/invitations/{id}/accept-as-default
        group
            .MapPost(
                "/{id}/accept-as-default",
                static (
                    string id,
                    ClaimsPrincipal user,
                    [FromServices] AcceptInvitationAsDefaultHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(id, user, ct)
            )
            .WithName("AcceptInvitationAsDefault")
            .WithTags("Invitations")
            .WithSummary("Accept an invitation as default.")
            .WithDescription(
                """
                Accepts the pending invitation (`id`) addressed to the authenticated user,
                then makes the invited organization the user's default organization.

                This is used by same-domain onboarding so the user lands in the team they
                joined after accepting.
                """
            );

        // POST /api/v1/me/invitations/{id}/decline
        group
            .MapPost(
                "/{id}/decline",
                static (
                    string id,
                    ClaimsPrincipal user,
                    [FromServices] DeclineInvitationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(id, user, ct)
            )
            .WithName("DeclineInvitation")
            .WithTags("Invitations")
            .WithSummary("Decline an invitation.")
            .WithDescription(
                """
                Declines the pending invitation (`id`) addressed to the authenticated user. No
                membership is created and the invitation is removed from their pending list.

                The user can only act on invitations addressed to their own email. This route
                intentionally remains outside current-org activation filtering so users can
                clear stale invitations even when their current org is inactive or unset.
                """
            );
    }
}
