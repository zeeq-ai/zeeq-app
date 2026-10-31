using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Documents;

namespace Zeeq.Data.Postgres.CodeReviews;

internal sealed partial class PostgresCodeRepositoryStore
{
    /// <inheritdoc />
    public async Task<CodeRepository> UpsertWithLibrariesAsync(
        CodeRepository repository,
        string[]? libraryIds,
        CancellationToken cancellationToken
    )
    {
        var originalLinks = repository.LibraryIds;
        try
        {
            return await RunMappingAsync(
                async () =>
                {
                    // Every existing library is locked in a stable order before the repository. This prevents
                    // a library deletion and a stale repository save from creating a dangling array reference.
                    var libraries = await db
                        .Libraries.FromSql(
                            $"""
                            SELECT * FROM zeeq.docs_libraries WHERE organization_id = {repository.OrganizationId}
                            ORDER BY id FOR KEY SHARE
                            """
                        )
                        .AsNoTracking()
                        .ToArrayAsync(cancellationToken);
                    var libraryById = libraries.ToDictionary(
                        library => library.Id,
                        StringComparer.Ordinal
                    );
                    var requested = libraryIds?.Distinct(StringComparer.Ordinal).ToArray();
                    if (requested is not null && requested.Any(id => !libraryById.ContainsKey(id)))
                        throw new ArgumentException(
                            "One or more library IDs are unknown or outside this organization.",
                            nameof(libraryIds)
                        );

                    var locked = (
                        await db
                            .CodeRepositories.FromSql(
                                $"""
                                SELECT * FROM zeeq.code_review_repositories
                                WHERE organization_id = {repository.OrganizationId} AND provider = {repository.Provider}
                                  AND owner_qualified_name = {repository.OwnerQualifiedName} AND disabled_at_utc IS NULL
                                FOR UPDATE
                                """
                            )
                            .AsNoTracking()
                            .ToArrayAsync(cancellationToken)
                    ).SingleOrDefault();
                    RepositoryLinkGuard.RejectPending(
                        db,
                        allowedNew: locked is null ? repository : null
                    );
                    var before = locked?.LibraryIds ?? [];
                    if (before.Any(id => !libraryById.ContainsKey(id)))
                        throw new MappingRetryException();
                    var after = requested ?? before;
                    if (locked is null)
                        repository.LibraryIds = [];
                    var saved = await UpsertAsync(repository, cancellationToken);
                    var beforeSet = before.ToHashSet(StringComparer.Ordinal);
                    var afterSet = after.ToHashSet(StringComparer.Ordinal);
                    foreach (var id in beforeSet.Union(afterSet))
                    {
                        if (
                            beforeSet.Contains(id) == afterSet.Contains(id)
                            || !libraryById.TryGetValue(id, out var library)
                        )
                            continue;
                        audit.AppendOrganization(
                            repository.OrganizationId,
                            library.TeamId,
                            LibraryAudit.Mapping(library, saved, afterSet.Contains(id))
                        );
                    }
                    saved.LibraryIds = after;
                    await db.SaveChangesAsync(cancellationToken);
                    return saved;
                },
                cancellationToken
            );
        }
        catch
        {
            repository.LibraryIds = originalLinks;
            throw;
        }
    }

    /// <inheritdoc />
    public Task<CodeRepository?> UpdateSettingsWithLibrariesAsync(
        string organizationId,
        string repositoryId,
        Zeeq.Platform.CodeReviews.RepositorySettingsPatch patch,
        string[]? libraryIds,
        CancellationToken cancellationToken
    ) =>
        RunMappingAsync(
            async () =>
            {
                var libraries = await db
                    .Libraries.FromSql(
                        $"""
                        SELECT * FROM zeeq.docs_libraries WHERE organization_id = {organizationId} ORDER BY id FOR KEY SHARE
                        """
                    )
                    .AsNoTracking()
                    .ToArrayAsync(cancellationToken);
                var libraryById = libraries.ToDictionary(
                    library => library.Id,
                    StringComparer.Ordinal
                );
                var desired = libraryIds?.Distinct(StringComparer.Ordinal).ToArray();
                if (desired is not null && desired.Any(id => !libraryById.ContainsKey(id)))
                    throw new ArgumentException(
                        "One or more library IDs are unknown or outside this organization.",
                        nameof(libraryIds)
                    );
                var locked = (
                    await db
                        .CodeRepositories.FromSql(
                            $"""
                            SELECT * FROM zeeq.code_review_repositories
                            WHERE organization_id = {organizationId} AND id = {repositoryId} AND disabled_at_utc IS NULL FOR UPDATE
                            """
                        )
                        .AsNoTracking()
                        .ToArrayAsync(cancellationToken)
                ).SingleOrDefault();
                if (locked is null)
                    return null;
                if (locked.LibraryIds.Any(id => !libraryById.ContainsKey(id)))
                    throw new MappingRetryException();
                RepositoryLinkGuard.RejectPending(db);
                var before = locked.LibraryIds.ToHashSet(StringComparer.Ordinal);
                var after = desired ?? locked.LibraryIds;
                var afterSet = after.ToHashSet(StringComparer.Ordinal);
                var name = string.IsNullOrWhiteSpace(patch.DisplayName)
                    ? locked.DisplayName
                    : patch.DisplayName.Trim();
                var visible = patch.VisibleInLibraryPicker ?? locked.VisibleInLibraryPicker;
                var updatedAt = Zeeq.Core.Common.PostgresTimestampPrecision.TruncateToMicroseconds(
                    DateTimeOffset.UtcNow
                );
                locked.DisplayName = name;
                foreach (var id in before.Union(afterSet))
                {
                    if (before.Contains(id) == afterSet.Contains(id))
                        continue;
                    audit.AppendOrganization(
                        organizationId,
                        libraryById[id].TeamId,
                        LibraryAudit.Mapping(libraryById[id], locked, afterSet.Contains(id))
                    );
                }
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE zeeq.code_review_repositories
                    SET team_id = {patch.TeamId}, display_name = {name}, enabled = {patch.Enabled},
                        visible_in_library_picker = {visible}, library_ids = {after}, updated_at_utc = {updatedAt}
                    WHERE organization_id = {organizationId} AND id = {repositoryId}
                    """,
                    cancellationToken
                );
                var tracked = db
                    .ChangeTracker.Entries<CodeRepository>()
                    .FirstOrDefault(e =>
                        e.Entity.OrganizationId == organizationId && e.Entity.Id == repositoryId
                    );
                if (tracked is not null)
                {
                    tracked.Property(e => e.TeamId).CurrentValue = patch.TeamId;
                    tracked.Property(e => e.TeamId).OriginalValue = patch.TeamId;
                    tracked.Property(e => e.TeamId).IsModified = false;
                    tracked.Property(e => e.DisplayName).CurrentValue = name;
                    tracked.Property(e => e.DisplayName).OriginalValue = name;
                    tracked.Property(e => e.DisplayName).IsModified = false;
                    tracked.Property(e => e.Enabled).CurrentValue = patch.Enabled;
                    tracked.Property(e => e.Enabled).OriginalValue = patch.Enabled;
                    tracked.Property(e => e.Enabled).IsModified = false;
                    tracked.Property(e => e.VisibleInLibraryPicker).CurrentValue = visible;
                    tracked.Property(e => e.VisibleInLibraryPicker).OriginalValue = visible;
                    tracked.Property(e => e.VisibleInLibraryPicker).IsModified = false;
                    tracked.Property(e => e.LibraryIds).CurrentValue = after;
                    tracked.Property(e => e.LibraryIds).OriginalValue = after;
                    tracked.Property(e => e.LibraryIds).IsModified = false;
                    tracked.Property(e => e.UpdatedAtUtc).CurrentValue = updatedAt;
                    tracked.Property(e => e.UpdatedAtUtc).OriginalValue = updatedAt;
                    tracked.Property(e => e.UpdatedAtUtc).IsModified = false;
                }
                await db.SaveChangesAsync(cancellationToken);
                locked.TeamId = patch.TeamId;
                locked.DisplayName = name;
                locked.Enabled = patch.Enabled;
                locked.VisibleInLibraryPicker = visible;
                locked.LibraryIds = after;
                locked.UpdatedAtUtc = updatedAt;
                return locked;
            },
            cancellationToken
        );

    /// <inheritdoc />
    public Task<CodeRepository?> ReplaceRepositoryLibrariesAsync(
        string organizationId,
        string repositoryId,
        string[] libraryIds,
        CancellationToken cancellationToken
    ) =>
        RunMappingAsync(
            async () =>
            {
                var libraries = await db
                    .Libraries.FromSql(
                        $"""
                        SELECT * FROM zeeq.docs_libraries WHERE organization_id = {organizationId} ORDER BY id FOR KEY SHARE
                        """
                    )
                    .AsNoTracking()
                    .ToArrayAsync(cancellationToken);
                var byId = libraries.ToDictionary(library => library.Id, StringComparer.Ordinal);
                var desired = libraryIds.Distinct(StringComparer.Ordinal).ToArray();
                if (desired.Any(id => !byId.ContainsKey(id)))
                    throw new ArgumentException(
                        "One or more library IDs are unknown or outside this organization.",
                        nameof(libraryIds)
                    );
                var repository = (
                    await db
                        .CodeRepositories.FromSql(
                            $"""
                            SELECT * FROM zeeq.code_review_repositories
                            WHERE organization_id = {organizationId} AND id = {repositoryId} AND disabled_at_utc IS NULL FOR UPDATE
                            """
                        )
                        .AsNoTracking()
                        .ToArrayAsync(cancellationToken)
                ).SingleOrDefault();
                if (repository is null)
                    return null;
                if (repository.LibraryIds.Any(id => !byId.ContainsKey(id)))
                    throw new MappingRetryException();
                RepositoryLinkGuard.RejectPending(db);
                var before = repository.LibraryIds.ToHashSet(StringComparer.Ordinal);
                var after = desired.ToHashSet(StringComparer.Ordinal);
                if (before.SetEquals(after))
                    return repository;
                foreach (var id in before.Union(after))
                {
                    if (
                        before.Contains(id) == after.Contains(id)
                        || !byId.TryGetValue(id, out var library)
                    )
                        continue;
                    audit.AppendOrganization(
                        organizationId,
                        library.TeamId,
                        LibraryAudit.Mapping(library, repository, after.Contains(id))
                    );
                }
                var now = DateTimeOffset.UtcNow;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE zeeq.code_review_repositories SET library_ids = {desired}, updated_at_utc = {now}
                    WHERE organization_id = {organizationId} AND id = {repositoryId}
                    """,
                    cancellationToken
                );
                var tracked = db
                    .ChangeTracker.Entries<CodeRepository>()
                    .FirstOrDefault(e =>
                        e.Entity.OrganizationId == organizationId && e.Entity.Id == repositoryId
                    );
                if (tracked is not null)
                {
                    tracked.Property(e => e.LibraryIds).CurrentValue = desired;
                    tracked.Property(e => e.LibraryIds).OriginalValue = desired;
                    tracked.Property(e => e.LibraryIds).IsModified = false;
                    tracked.Property(e => e.UpdatedAtUtc).CurrentValue = now;
                    tracked.Property(e => e.UpdatedAtUtc).OriginalValue = now;
                    tracked.Property(e => e.UpdatedAtUtc).IsModified = false;
                }
                await db.SaveChangesAsync(cancellationToken);
                repository.LibraryIds = desired;
                repository.UpdatedAtUtc = now;
                return repository;
            },
            cancellationToken
        );

    /// <inheritdoc />
    public Task ReplaceLibraryRepositoriesAsync(
        string organizationId,
        string libraryId,
        string[] repositoryIds,
        CancellationToken cancellationToken
    ) =>
        RunMappingAsync(
            async () =>
            {
                var library =
                    (
                        await db
                            .Libraries.FromSql(
                                $"""
                                SELECT * FROM zeeq.docs_libraries WHERE organization_id = {organizationId} AND id = {libraryId} FOR KEY SHARE
                                """
                            )
                            .AsNoTracking()
                            .ToArrayAsync(cancellationToken)
                    ).SingleOrDefault()
                    ?? throw new KeyNotFoundException("Library no longer exists.");
                // The target parent lock precedes repository locks. A different library deletion
                // may wait on these rows, then PostgreSQL rechecks its containment predicate;
                // this operation changes only the target library's membership in each locked row.
                var requestedIds = repositoryIds.Distinct(StringComparer.Ordinal).ToArray();
                var repositories = await db
                    .CodeRepositories.FromSql(
                        $"""
                        SELECT * FROM zeeq.code_review_repositories
                        WHERE organization_id = {organizationId} AND disabled_at_utc IS NULL
                          AND (library_ids @> ARRAY[{libraryId}]::text[] OR id = ANY({requestedIds}))
                        ORDER BY id FOR UPDATE
                        """
                    )
                    .AsNoTracking()
                    .ToArrayAsync(cancellationToken);
                var selected = repositoryIds.ToHashSet(StringComparer.Ordinal);
                var known = repositories.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                if (!selected.IsSubsetOf(known))
                    throw new ArgumentException(
                        "One or more repository IDs are unknown or outside this organization.",
                        nameof(repositoryIds)
                    );
                db.ChangeTracker.DetectChanges();
                // Link arrays are changed only through an audited boundary. Do not flush an
                // unrelated pending array mutation while saving this operation's facts.
                if (
                    db
                        .ChangeTracker.Entries<CodeRepository>()
                        .Any(entry =>
                            (
                                (
                                    entry.State == EntityState.Added
                                    && entry.Entity.LibraryIds.Length > 0
                                )
                                || (
                                    entry.State != EntityState.Added
                                    && (
                                        entry.Entity.OrganizationId != organizationId
                                        || !known.Contains(entry.Entity.Id)
                                    )
                                    && entry.Property(e => e.LibraryIds).IsModified
                                )
                            )
                        )
                )
                    throw new InvalidOperationException(
                        "An unrelated repository has pending library link changes."
                    );
                foreach (var row in repositories)
                {
                    var wasMapped = row.LibraryIds.Contains(libraryId, StringComparer.Ordinal);
                    var mapped = selected.Contains(row.Id);
                    var tracked = db
                        .ChangeTracker.Entries<CodeRepository>()
                        .FirstOrDefault(e =>
                            e.Entity.Id == row.Id && e.Entity.OrganizationId == organizationId
                        );
                    if (wasMapped == mapped)
                    {
                        if (tracked is not null)
                        {
                            tracked.Property(e => e.LibraryIds).CurrentValue = row.LibraryIds;
                            tracked.Property(e => e.LibraryIds).OriginalValue = row.LibraryIds;
                            tracked.Property(e => e.LibraryIds).IsModified = false;
                        }
                        continue;
                    }
                    var links = mapped
                        ? row.LibraryIds.Append(libraryId).ToArray()
                        : row.LibraryIds.Where(id => id != libraryId).ToArray();
                    var updatedAt =
                        Zeeq.Core.Common.PostgresTimestampPrecision.TruncateToMicroseconds(
                            DateTimeOffset.UtcNow
                        );
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        UPDATE zeeq.code_review_repositories SET library_ids = {links}, updated_at_utc = {updatedAt}
                        WHERE organization_id = {organizationId} AND id = {row.Id}
                        """,
                        cancellationToken
                    );
                    audit.AppendOrganization(
                        organizationId,
                        library.TeamId,
                        LibraryAudit.Mapping(library, row, mapped)
                    );
                    if (tracked is not null)
                    {
                        tracked.Property(e => e.LibraryIds).CurrentValue = links;
                        tracked.Property(e => e.LibraryIds).OriginalValue = links;
                        tracked.Property(e => e.LibraryIds).IsModified = false;
                        tracked.Property(e => e.UpdatedAtUtc).CurrentValue = updatedAt;
                        tracked.Property(e => e.UpdatedAtUtc).OriginalValue = updatedAt;
                        tracked.Property(e => e.UpdatedAtUtc).IsModified = false;
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
                return true;
            },
            cancellationToken
        );

    private async Task<T> RunMappingAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await RunMappingAttemptAsync(action, ct);
            }
            catch (MappingRetryException) when (attempt < 2) { }
            catch (MappingRetryException)
            {
                throw new Zeeq.Platform.CodeReviews.LibraryMappingConflictException();
            }
        }
        throw new Zeeq.Platform.CodeReviews.LibraryMappingConflictException();
    }

    private sealed class MappingRetryException : Exception;

    private async Task<T> RunMappingAttemptAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var ambient = db.Database.CurrentTransaction;
        await using var owned = ambient is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var savepoint = $"mapping_{Guid.NewGuid():N}";
        if (ambient is not null)
            await ambient.CreateSavepointAsync(savepoint, ct);
        var snapshots = db
            .ChangeTracker.Entries()
            .Select(entry => new
            {
                Entry = entry,
                entry.State,
                Current = entry.CurrentValues.Clone(),
                Original = entry.OriginalValues.Clone(),
                Modified = entry
                    .Properties.Where(p => p.IsModified)
                    .Select(p => p.Metadata.Name)
                    .ToArray(),
            })
            .ToArray();
        var prior = snapshots
            .Select(s => s.Entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            var result = await action();
            ct.ThrowIfCancellationRequested();
            if (ambient is not null)
                await ambient.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            if (owned is not null)
                await owned.CommitAsync(ct);
            return result;
        }
        catch
        {
            try
            {
                if (ambient is not null)
                    await ambient.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            }
            finally
            {
                foreach (
                    var entry in db
                        .ChangeTracker.Entries()
                        .Where(e => !prior.Contains(e.Entity))
                        .ToArray()
                )
                    entry.State = EntityState.Detached;
                foreach (var snapshot in snapshots)
                {
                    snapshot.Entry.CurrentValues.SetValues(snapshot.Current);
                    snapshot.Entry.OriginalValues.SetValues(snapshot.Original);
                    snapshot.Entry.State = snapshot.State;
                    if (snapshot.State == EntityState.Modified)
                        foreach (var property in snapshot.Entry.Properties)
                            property.IsModified = snapshot.Modified.Contains(
                                property.Metadata.Name
                            );
                }
            }
            throw;
        }
    }
}
