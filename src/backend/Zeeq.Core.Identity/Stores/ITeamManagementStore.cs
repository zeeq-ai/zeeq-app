namespace Zeeq.Core.Identity;

/// <summary>Persistence operations for organization-scoped custom teams.</summary>
public interface ITeamManagementStore
{
    /// <summary>Lists a page of teams and their active member counts.</summary>
    Task<TeamPage<TeamSummary>> ListTeamsAsync(
        string orgId,
        string? search,
        string? after,
        CancellationToken ct
    );

    /// <summary>Lists a page of custom teams for an active organization member.</summary>
    Task<TeamPage<TeamReference>> ListMemberTeamsAsync(
        string orgId,
        string userId,
        string? after,
        CancellationToken ct
    );

    /// <summary>Gets one custom team in the organization.</summary>
    Task<TeamSummary?> GetTeamAsync(string orgId, string teamId, CancellationToken ct);

    /// <summary>Lists roster members or eligible people who can be added.</summary>
    Task<TeamPage<TeamPerson>?> ListPeopleAsync(
        string orgId,
        string? teamId,
        bool candidates,
        string? search,
        string? after,
        CancellationToken ct
    );

    /// <summary>Creates a custom team and its initial assignments atomically.</summary>
    Task<CreateTeamResult> CreateAsync(
        string orgId,
        string actorId,
        string name,
        string? description,
        string[] userIds,
        CancellationToken ct
    );

    /// <summary>Applies additive member changes to a custom team.</summary>
    Task<UpdateTeamResult> UpdateAsync(
        string orgId,
        string teamId,
        string actorId,
        string[] add,
        string[] remove,
        CancellationToken ct
    );
}

/// <summary>A page of results and the cursor for loading more.</summary>
public sealed record TeamPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Team directory and detail information.</summary>
public sealed record TeamSummary(string Id, string Name, string? Description, int MemberCount);

/// <summary>A lightweight team used by the member lookup.</summary>
public sealed record TeamReference(string Id, string Name);

/// <summary>Profile information for a roster member or candidate.</summary>
public sealed record TeamPerson(
    string UserId,
    string DisplayName,
    string? Email,
    string? PictureUrl
);

/// <summary>Outcome of creating a custom team.</summary>
public closed record CreateTeamResult
{
    /// <summary>The new team was saved.</summary>
    public sealed record Saved(string TeamId) : CreateTeamResult;

    /// <summary>An active custom team already has this name.</summary>
    public sealed record NameTaken : CreateTeamResult;

    /// <summary>At least one proposed member is ineligible.</summary>
    public sealed record IneligibleMember : CreateTeamResult;
}

/// <summary>Outcome of changing a team's membership.</summary>
public closed record UpdateTeamResult
{
    /// <summary>The member changes were applied or already present.</summary>
    public sealed record Saved : UpdateTeamResult;

    /// <summary>The custom team is unavailable in this organization.</summary>
    public sealed record NotFound : UpdateTeamResult;

    /// <summary>At least one proposed member is ineligible.</summary>
    public sealed record IneligibleMember : UpdateTeamResult;
}
