using System.ComponentModel.DataAnnotations;

namespace Zeeq.Platform.Membership;

/// <summary>Organization team management and member directory endpoints.</summary>
public sealed class TeamEndpoints : IEndpoint
{
    /// <summary>Maps the team APIs under the active organization route.</summary>
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var organization = app.MapGroup("orgs/{orgId}")
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(SetupIdentityExtension.CookieScheme)
                    .RequireRole("owner", "admin")
            )
            .RequireRouteOrganizationMatchesCookie()
            .RequireActiveOrganization()
            .WithTags("Teams");

        var teams = organization.MapGroup("/teams");
        teams
            .MapGet(
                "",
                static (
                    string orgId,
                    string? q,
                    string? after,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.ListAsync(orgId, q, after, ct)
            )
            .WithName("ListTeams");
        teams
            .MapGet(
                "/candidates",
                static (
                    string orgId,
                    string? q,
                    string? after,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.PeopleAsync(orgId, null, true, q, after, ct)
            )
            .WithName("ListNewTeamCandidates");
        teams
            .MapGet(
                "/{teamId}",
                static (
                    string orgId,
                    string teamId,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.GetAsync(orgId, teamId, ct)
            )
            .WithName("GetTeam");
        teams
            .MapGet(
                "/{teamId}/members",
                static (
                    string orgId,
                    string teamId,
                    string? q,
                    string? after,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.PeopleAsync(orgId, teamId, false, q, after, ct)
            )
            .WithName("ListTeamMembers");
        teams
            .MapGet(
                "/{teamId}/candidates",
                static (
                    string orgId,
                    string teamId,
                    string? q,
                    string? after,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.PeopleAsync(orgId, teamId, true, q, after, ct)
            )
            .WithName("ListTeamCandidates");
        teams
            .MapPost(
                "",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    CreateTeamRequest request,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.CreateAsync(orgId, user, request, ct)
            )
            .WithName("CreateTeam");
        teams
            .MapPost(
                "/{teamId}/members",
                static (
                    string orgId,
                    string teamId,
                    ClaimsPrincipal user,
                    UpdateTeamMembersRequest request,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.UpdateAsync(orgId, teamId, user, request, ct)
            )
            .WithName("UpdateTeamMembers");

        organization
            .MapGet(
                "/members/{userId}/teams",
                static (
                    string orgId,
                    string userId,
                    string? after,
                    [FromServices] TeamManagementHandler handler,
                    CancellationToken ct
                ) => handler.ListMemberTeamsAsync(orgId, userId, after, ct)
            )
            .WithName("ListMemberTeams");
    }
}

/// <summary>Name, description, and initial members for a new custom team.</summary>
public sealed record CreateTeamRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: MaxLength(500)] string? Description,
    [property: MaxLength(100)] string[]? UserIds
);

/// <summary>User IDs to add to or remove from a team.</summary>
public sealed record UpdateTeamMembersRequest(
    [property: MaxLength(100)] string[]? Add,
    [property: MaxLength(100)] string[]? Remove
);

/// <summary>Identifies the team changed by a mutation.</summary>
public sealed record TeamMutationResponse(string TeamId);

/// <summary>A human-readable team management error.</summary>
public sealed record TeamError(string Message);
