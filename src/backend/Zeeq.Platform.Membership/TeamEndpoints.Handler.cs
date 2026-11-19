namespace Zeeq.Platform.Membership;

/// <summary>Validates team API inputs and dispatches to the team store.</summary>
public sealed class TeamManagementHandler(ITeamManagementStore store) : IEndpointHandler
{
    /// <summary>Lists custom teams in the active organization.</summary>
    public async Task<Results<Ok<TeamPage<TeamSummary>>, BadRequest<TeamError>>> ListAsync(
        string orgId,
        string? search,
        string? after,
        CancellationToken ct
    )
    {
        if (!ValidQuery(search, after))
            return TypedResults.BadRequest(new TeamError("Invalid search or cursor."));
        return TypedResults.Ok(await store.ListTeamsAsync(orgId, Clean(search), after, ct));
    }

    /// <summary>Lists the active custom teams for an organization member.</summary>
    public async Task<
        Results<Ok<TeamPage<TeamReference>>, BadRequest<TeamError>>
    > ListMemberTeamsAsync(string orgId, string userId, string? after, CancellationToken ct)
    {
        if (!ValidQuery(null, after))
            return TypedResults.BadRequest(new TeamError("Invalid cursor."));
        return TypedResults.Ok(await store.ListMemberTeamsAsync(orgId, userId, after, ct));
    }

    /// <summary>Gets one custom team in the active organization.</summary>
    public async Task<Results<Ok<TeamSummary>, NotFound>> GetAsync(
        string orgId,
        string teamId,
        CancellationToken ct
    )
    {
        var team = await store.GetTeamAsync(orgId, teamId, ct);
        return team is null ? TypedResults.NotFound() : TypedResults.Ok(team);
    }

    /// <summary>Lists existing or eligible members of a custom team.</summary>
    public async Task<
        Results<Ok<TeamPage<TeamPerson>>, NotFound, BadRequest<TeamError>>
    > PeopleAsync(
        string orgId,
        string? teamId,
        bool candidates,
        string? search,
        string? after,
        CancellationToken ct
    )
    {
        if (!ValidQuery(search, after))
            return TypedResults.BadRequest(new TeamError("Invalid search or cursor."));
        var people = await store.ListPeopleAsync(
            orgId,
            teamId,
            candidates,
            Clean(search),
            after,
            ct
        );
        return people is null ? TypedResults.NotFound() : TypedResults.Ok(people);
    }

    /// <summary>Creates a custom team with optional initial members.</summary>
    public async Task<
        Results<Created<TeamMutationResponse>, BadRequest<TeamError>, Conflict<TeamError>>
    > CreateAsync(
        string orgId,
        ClaimsPrincipal user,
        CreateTeamRequest request,
        CancellationToken ct
    )
    {
        var name = Clean(request.Name);
        var description = Clean(request.Description);
        var ids = request.UserIds ?? [];
        if (name is null || name.Length > 200 || description?.Length > 500 || !ValidIds(ids))
            return TypedResults.BadRequest(
                new TeamError("Enter a valid team name and up to 100 member IDs.")
            );

        var actor = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var result = await store.CreateAsync(
            orgId,
            actor,
            name,
            description,
            ids.Distinct().ToArray(),
            ct
        );
        return result switch
        {
            CreateTeamResult.Saved(var id) => TypedResults.Created(
                $"/api/v1/orgs/{orgId}/teams/{id}",
                new TeamMutationResponse(id)
            ),
            CreateTeamResult.NameTaken => TypedResults.Conflict(
                new TeamError("A team with that name already exists.")
            ),
            CreateTeamResult.IneligibleMember => TypedResults.BadRequest(
                new TeamError("Only active organization members can be added.")
            ),
        };
    }

    /// <summary>Applies a delta to the members of a custom team.</summary>
    public async Task<
        Results<Ok<TeamMutationResponse>, NotFound, BadRequest<TeamError>>
    > UpdateAsync(
        string orgId,
        string teamId,
        ClaimsPrincipal user,
        UpdateTeamMembersRequest request,
        CancellationToken ct
    )
    {
        var requestedAdd = request.Add ?? [];
        var requestedRemove = request.Remove ?? [];
        if (!ValidIds(requestedAdd) || !ValidIds(requestedRemove))
            return TypedResults.BadRequest(
                new TeamError("Supply up to 100 valid user IDs in each list.")
            );

        var additions = requestedAdd.ToHashSet(StringComparer.Ordinal);
        var removals = requestedRemove.ToHashSet(StringComparer.Ordinal);
        if (additions.Overlaps(removals))
            return TypedResults.BadRequest(
                new TeamError("A member cannot be both added and removed.")
            );

        var actor = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var result = await store.UpdateAsync(
            orgId,
            teamId,
            actor,
            additions.ToArray(),
            removals.ToArray(),
            ct
        );
        return result switch
        {
            UpdateTeamResult.Saved => TypedResults.Ok(new TeamMutationResponse(teamId)),
            UpdateTeamResult.NotFound => TypedResults.NotFound(),
            UpdateTeamResult.IneligibleMember => TypedResults.BadRequest(
                new TeamError("Only active organization members can be added.")
            ),
        };
    }

    private static bool ValidQuery(string? search, string? after) =>
        (search is null || search.Length <= 200)
        && (after is null || after.Length is > 0 and <= 200);

    private static bool ValidIds(string[] ids) =>
        ids.Length <= 100 && ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
