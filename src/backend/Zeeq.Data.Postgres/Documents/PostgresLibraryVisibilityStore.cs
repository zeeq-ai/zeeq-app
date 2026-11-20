using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;

namespace Zeeq.Data.Postgres.Documents;

internal sealed class PostgresLibraryVisibilityStore(
    PostgresDbContext db,
    PostgresLibraryDocumentStore documents,
    IAuditTrailWriter audit
) : ILibraryVisibilityStore
{
    private DbSet<LibraryTeamGrant> Grants => db.Set<LibraryTeamGrant>();

    private IQueryable<Library> VisibleLibraries(LibraryAudience audience)
    {
        var libraries = db
            .Libraries.AsNoTracking()
            .Where(library => library.OrganizationId == audience.OrganizationId);

        if (audience.IsAdministrator)
            return libraries;

        var memberships =
            from membership in db.TeamMemberships
            join team in db.Teams
                on new { membership.OrganizationId, membership.TeamId } equals new
                {
                    team.OrganizationId,
                    TeamId = team.Id,
                }
            where
                membership.OrganizationId == audience.OrganizationId
                && membership.UserId == audience.UserId
                && membership.DisabledAtUtc == null
                && team.DisabledAtUtc == null
            select membership.TeamId;

        var grants = Grants.Where(grant => grant.OrganizationId == audience.OrganizationId);
        return libraries.Where(library =>
            !grants.Any(grant => grant.LibraryId == library.Id)
            || grants.Any(grant =>
                grant.LibraryId == library.Id && memberships.Contains(grant.TeamId)
            )
        );
    }

    public Task<bool> CanOpenAsync(
        LibraryAudience audience,
        string libraryName,
        CancellationToken ct
    ) =>
        VisibleLibraries(audience)
            .TagWithOperationCallSite("library.visibility.open")
            .AnyAsync(library => library.Name == libraryName, ct);

    public async Task<IReadOnlyList<Library>> ListVisibleAsync(
        LibraryAudience audience,
        CancellationToken ct
    ) =>
        await VisibleLibraries(audience)
            .TagWithOperationCallSite("library.visibility.list")
            .OrderBy(library => library.Name)
            .ToArrayAsync(ct);

    public Task<string[]> ListVisibleNamesAsync(LibraryAudience audience, CancellationToken ct) =>
        VisibleLibraries(audience)
            .TagWithOperationCallSite("library.visibility.names")
            .Select(library => library.Name)
            .ToArrayAsync(ct);

    public async Task<LibraryVisibilitySettings?> GetSettingsAsync(
        string organizationId,
        string libraryName,
        CancellationToken ct
    )
    {
        var library = db.Libraries.Where(row =>
            row.OrganizationId == organizationId && row.Name == libraryName
        );
        var settings = await library
            .Select(row => new
            {
                TeamIds = Grants
                    .Where(grant =>
                        grant.OrganizationId == organizationId && grant.LibraryId == row.Id
                    )
                    .Select(grant => grant.TeamId)
                    .ToArray(),
                Teams = db
                    .Teams.Where(team =>
                        team.OrganizationId == organizationId
                        && (
                            !team.IsRootTeam && team.DisabledAtUtc == null
                            || Grants.Any(grant =>
                                grant.OrganizationId == organizationId
                                && grant.LibraryId == row.Id
                                && grant.TeamId == team.Id
                            )
                        )
                    )
                    .OrderBy(team => team.DisplayName)
                    .Select(team => new LibraryVisibilityTeam(
                        team.Id,
                        team.DisplayName,
                        !team.IsRootTeam && team.DisabledAtUtc == null
                    ))
                    .ToArray(),
            })
            .SingleOrDefaultAsync(ct);

        return settings is null ? null : new(settings.TeamIds, settings.Teams);
    }

    public Task<(LibraryVisibilitySaveStatus Status, Library? Library)> SaveAsync(
        Library library,
        LibraryVisibilityChange change,
        bool requestFullResync,
        CancellationToken ct
    ) =>
        AuditMutationCheckpoint.RunAsync(
            db,
            () => SaveWithinTransactionAsync(library, change, requestFullResync, ct),
            ct
        );

    private async Task<(
        LibraryVisibilitySaveStatus Status,
        Library? Library
    )> SaveWithinTransactionAsync(
        Library library,
        LibraryVisibilityChange change,
        bool requestFullResync,
        CancellationToken ct
    )
    {
        var selected = change.TeamIds.ToHashSet(StringComparer.Ordinal);
        var organizationId = library.OrganizationId;
        var libraryId = library.Id;

        var locked = await db
            .Database.SqlQuery<string>(
                $"""
                SELECT id AS "Value" FROM zeeq.docs_libraries
                WHERE organization_id = {organizationId} AND id = {libraryId}
                FOR UPDATE
                """
            )
            .ToArrayAsync(ct);
        if (locked.Length == 0)
            return (LibraryVisibilitySaveStatus.Missing, null);

        var grants = Grants.Where(grant =>
            grant.OrganizationId == organizationId && grant.LibraryId == libraryId
        );
        var current = (await grants.Select(grant => grant.TeamId).ToArrayAsync(ct)).ToHashSet(
            StringComparer.Ordinal
        );
        if (!current.SetEquals(change.ExpectedTeamIds))
            return (LibraryVisibilitySaveStatus.Conflict, null);
        if (current.Count > 0 && selected.Count == 0 && !change.ConfirmEveryone)
            return (LibraryVisibilitySaveStatus.ConfirmationRequired, null);

        var additions = selected.Except(current).Order(StringComparer.Ordinal).ToArray();
        if (additions.Length > 0)
        {
            var valid = await db
                .Database.SqlQuery<string>(
                    $"""
                    SELECT id AS "Value" FROM zeeq.core_teams
                    WHERE organization_id = {organizationId} AND id = ANY({additions})
                        AND NOT is_root_team AND disabled_at_utc IS NULL
                    ORDER BY id FOR SHARE
                    """
                )
                .ToArrayAsync(ct);
            if (valid.Length != additions.Length)
                return (LibraryVisibilitySaveStatus.InvalidTeams, null);
        }

        // The initial lookup already locked this row. A missing update is an invariant
        // violation, so fail the transaction instead of committing a partial save.
        var updated = await documents.TryUpdateLibraryAsync(library, requestFullResync, ct)
            ?? throw new InvalidOperationException("The locked library could not be updated.");

        var removals = current.Except(selected).ToArray();
        if (additions.Length == 0 && removals.Length == 0)
            return (LibraryVisibilitySaveStatus.Saved, updated);

        var affectedTeams = current.Union(selected).ToArray();
        var teamNames = await db
            .Teams.AsNoTracking()
            .Where(team => team.OrganizationId == organizationId && affectedTeams.Contains(team.Id))
            .Select(team => new
            {
                team.Id,
                Label = team.DisabledAtUtc == null
                    ? team.DisplayName
                    : team.DisplayName + " (archived)",
            })
            .ToDictionaryAsync(team => team.Id, team => team.Label, ct);

        if (removals.Length > 0)
            await grants.Where(grant => removals.Contains(grant.TeamId)).ExecuteDeleteAsync(ct);

        var addedGrants = additions
            .Select(teamId => new LibraryTeamGrant
            {
                OrganizationId = organizationId,
                LibraryId = libraryId,
                TeamId = teamId,
            })
            .ToArray();
        Grants.AddRange(addedGrants);
        audit.AppendOrganization(
            organizationId,
            updated.TeamId,
            LibraryAudit.Visibility(updated, current, selected, teamNames)
        );
        await db.SaveChangesAsync(ct);

        // Grants are replaced using set-based deletes. Keep the context free of tracked grant rows.
        foreach (var grant in addedGrants)
            db.Entry(grant).State = EntityState.Detached;

        return (LibraryVisibilitySaveStatus.Saved, updated);
    }
}
