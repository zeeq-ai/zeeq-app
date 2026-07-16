using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Endpoints for managing members within an organization.
/// </summary>
public static class OrganizationMembersExtensions
{
    /// <summary>
    /// Maps organization member and invitation endpoints.
    /// </summary>
    public static RouteGroupBuilder MapOrganizationMemberEndpoints(this RouteGroupBuilder group)
    {
        // GET /api/v1/orgs/{orgId}/members
        group
            .MapGet(
                "/members",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListMembersHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("ListMembers")
            .WithTags("Organizations")
            .WithSummary("List organization members.")
            .WithDescription(
                """
                Returns the active members of the organization (`orgId`) along with each
                member's role. Pending invitees are not included here — see the invitations
                endpoints for those.
                """
            );

        // PUT /api/v1/orgs/{orgId}/members/{userId}
        group
            .MapPut(
                "/members/{userId}",
                static (
                    string orgId,
                    string userId,
                    ChangeMemberRoleRequest request,
                    [FromServices] ChangeMemberRoleHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, userId, request.Role, ct)
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .RequireActiveOrganization()
            .WithName("ChangeMemberRole")
            .WithTags("Organizations")
            .WithSummary("Change a member's role.")
            .WithDescription(
                """
                Sets the role of the member (`userId`) within the organization (`orgId`) to the
                role in the request body.

                Requires the `owner` or `admin` role in the organization.
                """
            );

        // DELETE /api/v1/orgs/{orgId}/members/{userId}
        group
            .MapDelete(
                "/members/{userId}",
                static (
                    string orgId,
                    string userId,
                    [FromServices] RemoveMemberHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, userId, ct)
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .RequireActiveOrganization()
            .WithName("RemoveMember")
            .WithTags("Organizations")
            .WithSummary("Remove a member.")
            .WithDescription(
                """
                Removes the member (`userId`) from the organization (`orgId`), revoking their
                access. To remove yourself, use the leave-organization endpoint instead.

                Requires the `owner` or `admin` role in the organization.
                """
            );

        group.MapInvitationEndpoints();
        return group;
    }
}
