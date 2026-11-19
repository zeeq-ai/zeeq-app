using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Auditing;

namespace Zeeq.Data.Postgres.Identity;

internal sealed class PostgresTeamManagementStore(PostgresDbContext db) : ITeamManagementStore
{
    private const int PageSize = 40;

    public async Task<TeamPage<TeamSummary>> ListTeamsAsync(
        string orgId,
        string? search,
        string? after,
        CancellationToken ct
    )
    {
        var query = Teams(orgId);
        if (search is not null)
        {
            var pattern = SearchPattern(search);
            query = query.Where(t => EF.Functions.ILike(t.DisplayName, pattern, "\\"));
        }
        // Match the directory index's database-default collation for paging and ordering.
        var directory = query.Select(team => new
        {
            Team = team,
            Cursor = EF.Functions.Collate(EF.Property<string>(team, "NormalizedName"), "default"),
        });
        if (after is not null)
            directory = directory.Where(row => string.Compare(row.Cursor, after) > 0);

        var rows = await directory
            .TagWithOperationCallSite("membership.team.list")
            .OrderBy(row => row.Cursor)
            .Take(PageSize + 1)
            .ToArrayAsync(ct);
        var page = rows.Take(PageSize).ToArray();
        return new TeamPage<TeamSummary>(
            await SummarizeAsync(orgId, page.Select(row => row.Team).ToArray(), ct),
            rows.Length > PageSize ? page[^1].Cursor : null
        );
    }

    public async Task<TeamPage<TeamReference>> ListMemberTeamsAsync(
        string orgId,
        string userId,
        string? after,
        CancellationToken ct
    )
    {
        // The membership primary key guarantees one row per organization, team and user.
        var assignments = db
            .TeamMemberships.AsNoTracking()
            .TagWithOperationCallSite("membership.team.list_member_teams")
            .Where(m => m.OrganizationId == orgId && m.UserId == userId && m.DisabledAtUtc == null);
        var teams = assignments
            .Join(
                ActiveMembers(orgId),
                assignment => assignment.UserId,
                member => member.UserId,
                (assignment, _) => assignment
            )
            .Join(
                db.Users.Where(u => u.DisabledAtUtc == null),
                assignment => assignment.UserId,
                user => user.Id,
                (assignment, _) => assignment
            )
            .Join(
                Teams(orgId),
                assignment => assignment.TeamId,
                team => team.Id,
                (assignment, team) => new { assignment.TeamId, Name = team.DisplayName }
            );
        if (after is not null)
            teams = teams.Where(team => string.Compare(team.TeamId, after) > 0);

        var rows = await teams
            .OrderBy(team => team.TeamId)
            .Take(PageSize + 1)
            .Select(team => new TeamReference(team.TeamId, team.Name))
            .ToArrayAsync(ct);
        var page = rows.Take(PageSize).ToArray();
        return new TeamPage<TeamReference>(page, rows.Length > PageSize ? page[^1].Id : null);
    }

    public async Task<TeamSummary?> GetTeamAsync(string orgId, string teamId, CancellationToken ct)
    {
        var team = await Teams(orgId)
            .TagWithOperationCallSite("membership.team.get")
            .SingleOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null)
            return null;

        return (await SummarizeAsync(orgId, [team], ct))[0];
    }

    public async Task<TeamPage<TeamPerson>?> ListPeopleAsync(
        string orgId,
        string? teamId,
        bool candidates,
        string? search,
        string? after,
        CancellationToken ct
    )
    {
        if (teamId is not null && !await Teams(orgId).AnyAsync(t => t.Id == teamId, ct))
            return null;

        var members = ActiveMembers(orgId);
        if (teamId is not null)
        {
            var assigned = db.TeamMemberships.Where(m =>
                m.OrganizationId == orgId && m.TeamId == teamId && m.DisabledAtUtc == null
            );
            members = candidates
                ? members.Where(m => !assigned.Any(a => a.UserId == m.UserId))
                : members.Where(m => assigned.Any(a => a.UserId == m.UserId));
        }

        var people = members.Join(
            db.Users.AsNoTracking().Where(u => u.DisabledAtUtc == null),
            member => member.UserId,
            user => user.Id,
            (_, user) =>
                new
                {
                    UserId = user.Id,
                    user.DisplayName,
                    user.Email,
                    user.PictureUrl,
                }
        );
        if (after is not null)
            people = people.Where(person => string.Compare(person.UserId, after) > 0);
        if (search is not null)
        {
            var pattern = SearchPattern(search);
            people = people.Where(person =>
                EF.Functions.ILike(person.DisplayName, pattern, "\\")
                || (person.Email != null && EF.Functions.ILike(person.Email, pattern, "\\"))
            );
        }

        var rows = await people
            .TagWithOperationCallSite("membership.team.list_people")
            .OrderBy(person => person.UserId)
            .Take(PageSize + 1)
            .Select(person => new TeamPerson(
                person.UserId,
                person.DisplayName,
                person.Email,
                person.PictureUrl
            ))
            .ToArrayAsync(ct);
        var page = rows.Take(PageSize).ToArray();
        return new TeamPage<TeamPerson>(page, rows.Length > PageSize ? page[^1].UserId : null);
    }

    public Task<CreateTeamResult> CreateAsync(
        string orgId,
        string actorId,
        string name,
        string? description,
        string[] userIds,
        CancellationToken ct
    ) =>
        AuditMutationCheckpoint.RunAsync<CreateTeamResult>(
            db,
            async () =>
            {
                if (!await LockEligibleMembersAsync(orgId, userIds, ct))
                    return new CreateTeamResult.IneligibleMember();

                var teamId = "team_" + Guid.NewGuid().ToString("N");
                var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO zeeq.core_teams
                        (id, organization_id, display_name, description, is_root_team,
                            created_by_user_id, created_at_utc, updated_at_utc)
                    VALUES ({teamId}, {orgId}, {name}, {description}, false, {actorId}, now(), now())
                    ON CONFLICT (organization_id, normalized_name)
                        WHERE NOT is_root_team AND disabled_at_utc IS NULL
                    DO NOTHING
                    """,
                    ct
                );
                if (inserted == 0)
                    return new CreateTeamResult.NameTaken();

                await AddMembersAsync(orgId, teamId, actorId, userIds, ct);
                return new CreateTeamResult.Saved(teamId);
            },
            ct
        );

    public Task<UpdateTeamResult> UpdateAsync(
        string orgId,
        string teamId,
        string actorId,
        string[] add,
        string[] remove,
        CancellationToken ct
    ) =>
        AuditMutationCheckpoint.RunAsync<UpdateTeamResult>(
            db,
            async () =>
            {
                var teams = await db
                    .Database.SqlQuery<string>(
                        $"""
                        SELECT id AS "Value" FROM zeeq.core_teams
                        WHERE organization_id = {orgId} AND id = {teamId}
                            AND NOT is_root_team AND disabled_at_utc IS NULL
                        FOR UPDATE
                        """
                    )
                    .ToArrayAsync(ct);
                if (teams.Length == 0)
                    return new UpdateTeamResult.NotFound();
                if (!await LockEligibleMembersAsync(orgId, add, ct))
                    return new UpdateTeamResult.IneligibleMember();

                await AddMembersAsync(orgId, teamId, actorId, add, ct);
                if (remove.Length != 0)
                {
                    var now = DateTimeOffset.UtcNow;
                    await db
                        .TeamMemberships.TagWithOperationCallSite("membership.team.remove_members")
                        .Where(m =>
                            m.OrganizationId == orgId
                            && m.TeamId == teamId
                            && m.DisabledAtUtc == null
                            && remove.Contains(m.UserId)
                        )
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(m => m.DisabledAtUtc, now),
                            ct
                        );
                }
                return new UpdateTeamResult.Saved();
            },
            ct
        );

    private IQueryable<Team> Teams(string orgId) =>
        db
            .Teams.AsNoTracking()
            .Where(t => t.OrganizationId == orgId && !t.IsRootTeam && t.DisabledAtUtc == null);

    private IQueryable<OrganizationMembership> ActiveMembers(string orgId) =>
        db
            .OrganizationMemberships.AsNoTracking()
            .Where(m =>
                m.OrganizationId == orgId
                && m.Status == MembershipStatus.Active
                && m.DisabledAtUtc == null
                && m.UserId != null
            );

    private async Task<IReadOnlyList<TeamSummary>> SummarizeAsync(
        string orgId,
        Team[] teams,
        CancellationToken ct
    )
    {
        if (teams.Length == 0)
            return [];

        var ids = teams.Select(t => t.Id).ToArray();
        var activeUsers = ActiveMembers(orgId)
            .Join(
                db.Users.Where(u => u.DisabledAtUtc == null),
                member => member.UserId,
                user => user.Id,
                (_, user) => user.Id
            );
        var counts = await db
            .TeamMemberships.AsNoTracking()
            .TagWithOperationCallSite("membership.team.count_members")
            .Where(m =>
                m.OrganizationId == orgId
                && m.DisabledAtUtc == null
                && ids.Contains(m.TeamId)
                && activeUsers.Contains(m.UserId)
            )
            .GroupBy(m => m.TeamId)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Id, row => row.Count, ct);
        return teams
            .Select(t => new TeamSummary(
                t.Id,
                t.DisplayName,
                t.Description,
                counts.GetValueOrDefault(t.Id)
            ))
            .ToArray();
    }

    private async Task<bool> LockEligibleMembersAsync(
        string orgId,
        string[] userIds,
        CancellationToken ct
    )
    {
        if (userIds.Length == 0)
            return true;

        // Organization removal takes the same lock before disabling assignments.
        var eligible = await db
            .Database.SqlQuery<string>(
                $"""
                SELECT m.user_id AS "Value"
                FROM zeeq.core_organization_memberships m
                JOIN zeeq.core_users u ON u.id = m.user_id AND u.disabled_at_utc IS NULL
                WHERE m.organization_id = {orgId} AND m.status = 'Active'
                    AND m.disabled_at_utc IS NULL AND m.user_id = ANY({userIds})
                ORDER BY m.user_id
                FOR UPDATE OF m
                """
            )
            .ToArrayAsync(ct);
        return eligible.Length == userIds.Length;
    }

    private async Task AddMembersAsync(
        string orgId,
        string teamId,
        string actorId,
        string[] userIds,
        CancellationToken ct
    )
    {
        if (userIds.Length == 0)
            return;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO zeeq.core_team_memberships
                (organization_id, team_id, user_id, role, created_by_user_id, created_at_utc)
            SELECT {orgId}, {teamId}, user_id, 'member', {actorId}, now()
            FROM unnest({userIds}) AS incoming(user_id)
            ORDER BY user_id
            ON CONFLICT (organization_id, team_id, user_id) DO UPDATE
            SET disabled_at_utc = NULL
            WHERE core_team_memberships.disabled_at_utc IS NOT NULL
            """,
            ct
        );
    }

    private static string SearchPattern(string search) =>
        "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
