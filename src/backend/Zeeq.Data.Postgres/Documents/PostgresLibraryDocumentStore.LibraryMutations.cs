using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;

namespace Zeeq.Data.Postgres.Documents;

internal sealed partial class PostgresLibraryDocumentStore
{
    /// <inheritdoc />
    public async Task<Library> CreateLibraryAsync(Library library, CancellationToken ct)
    {
        var existingFacts = db
            .ChangeTracker.Entries<OrganizationAuditEvent>()
            .Select(e => e.Entity)
            .ToHashSet();
        var wasTracked = db.Entry(library).State != EntityState.Detached;
        try
        {
            audit.AppendOrganization(
                library.OrganizationId,
                library.TeamId,
                LibraryAudit.Change(
                    "library.created",
                    AuditChangeType.Added,
                    library,
                    null,
                    LibraryAudit.Snapshot(library),
                    []
                )
            );
            db.Libraries.Add(library);
            await db.SaveChangesAsync(ct);
            return library;
        }
        catch
        {
            foreach (
                var entry in db
                    .ChangeTracker.Entries<OrganizationAuditEvent>()
                    .Where(e => !existingFacts.Contains(e.Entity))
                    .ToArray()
            )
                entry.State = EntityState.Detached;
            if (!wasTracked)
                db.Entry(library).State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
        await TryUpdateLibraryAsync(library, requestFullResync: false, ct)
        ?? throw new KeyNotFoundException("Library no longer exists.");

    /// <inheritdoc />
    public async Task<Library?> TryUpdateLibraryAsync(
        Library library,
        bool requestFullResync,
        CancellationToken ct
    )
    {
        var requested = (
            library.Name,
            library.Description,
            library.IncludeFilters,
            library.ExcludeFilters,
            library.UpdatedAt
        );
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        var locked = (
            await db
                .Libraries.FromSql(
                    $"""
                    SELECT * FROM zeeq.docs_libraries
                    WHERE organization_id = {library.OrganizationId} AND id = {library.Id} FOR UPDATE
                    """
                )
                .AsNoTracking()
                .ToArrayAsync(ct)
        ).SingleOrDefault();
        if (locked is null)
        {
            await CommitAsync(transaction, ct);
            return null;
        }
        var original = (
            locked.Name,
            locked.Description,
            locked.IncludeFilters,
            locked.ExcludeFilters,
            locked.UpdatedAt,
            locked.NextFullResyncAt
        );
        var ambient = db.Database.CurrentTransaction;
        // `transaction` is non-null only when this method owns it. An ambient caller may catch
        // any exception, so savepoint rollback must undo a successful SaveChanges as well.
        var savepoint = $"library_update_{Guid.NewGuid():N}";
        if (transaction is null && ambient is not null)
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
                    .Properties.Where(property => property.IsModified)
                    .Select(property => property.Metadata.Name)
                    .ToArray(),
            })
            .ToArray();
        var priorEntities = snapshots
            .Select(snapshot => snapshot.Entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var existingFacts = db
            .ChangeTracker.Entries<OrganizationAuditEvent>()
            .Select(e => e.Entity)
            .ToHashSet();
        try
        {
            var fields = new List<string>();
            if (locked.Name != requested.Name)
                fields.Add("name");
            if (locked.Description != requested.Description)
                fields.Add("description");
            if (!locked.IncludeFilters.SequenceEqual(requested.IncludeFilters))
                fields.Add("includeFilters");
            if (!locked.ExcludeFilters.SequenceEqual(requested.ExcludeFilters))
                fields.Add("excludeFilters");
            if (fields.Count > 0)
            {
                var after = new Library
                {
                    Id = locked.Id,
                    OrganizationId = locked.OrganizationId,
                    TeamId = locked.TeamId,
                    Name = requested.Name,
                    Description = requested.Description,
                    IncludeFilters = requested.IncludeFilters,
                    ExcludeFilters = requested.ExcludeFilters,
                    SourceKind = locked.SourceKind,
                    PublicSourceId = locked.PublicSourceId,
                };
                audit.AppendOrganization(
                    locked.OrganizationId,
                    locked.TeamId,
                    LibraryAudit.Change(
                        "library.updated",
                        AuditChangeType.Edited,
                        after,
                        LibraryAudit.Snapshot(locked),
                        LibraryAudit.Snapshot(after),
                        fields
                    )
                );
            }
            var entry = db
                .ChangeTracker.Entries<Library>()
                .FirstOrDefault(e =>
                    e.Entity.OrganizationId == locked.OrganizationId && e.Entity.Id == locked.Id
                );
            if (entry is null)
                entry = db.Libraries.Attach(locked);
            // Refresh only fields owned by this operation; EF scalar Reload is unsafe for owned JSON.
            entry.Property(row => row.Name).OriginalValue = locked.Name;
            entry.Property(row => row.Description).OriginalValue = locked.Description;
            entry.Property(row => row.IncludeFilters).OriginalValue = locked.IncludeFilters;
            entry.Property(row => row.ExcludeFilters).OriginalValue = locked.ExcludeFilters;
            entry.Property(row => row.UpdatedAt).OriginalValue = locked.UpdatedAt;
            entry.Property(row => row.NextFullResyncAt).OriginalValue = locked.NextFullResyncAt;
            entry.Property(row => row.Name).CurrentValue = requested.Name;
            entry.Property(row => row.Description).CurrentValue = requested.Description;
            entry.Property(row => row.IncludeFilters).CurrentValue = requested.IncludeFilters;
            entry.Property(row => row.ExcludeFilters).CurrentValue = requested.ExcludeFilters;
            entry.Property(row => row.UpdatedAt).CurrentValue = requested.UpdatedAt;
            entry.Property(row => row.NextFullResyncAt).CurrentValue =
                requestFullResync && locked.SourceKind == RepositorySourceKind.Notion.ToString()
                    ? DateTimeOffset.UtcNow
                    : original.NextFullResyncAt;
            await db.SaveChangesAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (transaction is null && ambient is not null)
                await ambient.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            await CommitAsync(transaction, ct);
            return entry.Entity;
        }
        catch
        {
            if (transaction is null && ambient is not null)
                await ambient.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            foreach (
                var fact in db
                    .ChangeTracker.Entries<OrganizationAuditEvent>()
                    .Where(e => !existingFacts.Contains(e.Entity))
                    .ToArray()
            )
                fact.State = EntityState.Detached;
            foreach (
                var entry in db
                    .ChangeTracker.Entries()
                    .Where(e => !priorEntities.Contains(e.Entity))
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
                        property.IsModified = snapshot.Modified.Contains(property.Metadata.Name);
            }
            // A caller can pass a tracked object it has already edited. Restore only the fields
            // this operation owns so a later SaveChanges cannot bypass the failed audit append.
            var tracked = db
                .ChangeTracker.Entries<Library>()
                .FirstOrDefault(e =>
                    e.Entity.OrganizationId == locked.OrganizationId && e.Entity.Id == locked.Id
                );
            if (tracked is not null)
            {
                tracked.Property(row => row.Name).CurrentValue = original.Name;
                tracked.Property(row => row.Name).OriginalValue = original.Name;
                tracked.Property(row => row.Description).CurrentValue = original.Description;
                tracked.Property(row => row.Description).OriginalValue = original.Description;
                tracked.Property(row => row.IncludeFilters).CurrentValue = original.IncludeFilters;
                tracked.Property(row => row.IncludeFilters).OriginalValue = original.IncludeFilters;
                tracked.Property(row => row.ExcludeFilters).CurrentValue = original.ExcludeFilters;
                tracked.Property(row => row.ExcludeFilters).OriginalValue = original.ExcludeFilters;
                tracked.Property(row => row.UpdatedAt).CurrentValue = original.UpdatedAt;
                tracked.Property(row => row.UpdatedAt).OriginalValue = original.UpdatedAt;
                tracked.Property(row => row.NextFullResyncAt).CurrentValue =
                    original.NextFullResyncAt;
                tracked.Property(row => row.NextFullResyncAt).OriginalValue =
                    original.NextFullResyncAt;
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteLibraryAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    )
    {
        var ambient = db.Database.CurrentTransaction;
        Zeeq.Data.Postgres.CodeReviews.RepositoryLinkGuard.RejectPending(db);
        if (ambient is not null && db.ChangeTracker.HasChanges())
        {
            // Flush before the deletion savepoint so its rollback preserves caller work even
            // when EF has already accepted that work in the change tracker. The caller still owns
            // and may roll back the enclosing transaction; this method never commits it.
            // Pending document edits must not acquire child locks before the parent lock.
            // A new unsaved library has no competing committed parent and can be flushed first.
            var isNew = db
                .ChangeTracker.Entries<Library>()
                .Any(entry =>
                    entry.Entity.OrganizationId == organizationId
                    && entry.Entity.Id == libraryId
                    && entry.State == EntityState.Added
                );
            if (!isNew)
                await db
                    .Database.SqlQuery<string>(
                        $"""
                        SELECT id AS "Value" FROM zeeq.docs_libraries
                        WHERE organization_id = {organizationId} AND id = {libraryId} FOR UPDATE
                        """
                    )
                    .ToArrayAsync(ct);
            await db.SaveChangesAsync(ct);
        }
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        var savepoint = $"library_delete_{Guid.NewGuid():N}";
        if (ambient is not null)
            await ambient.CreateSavepointAsync(savepoint, ct);
        var existingFacts = db
            .ChangeTracker.Entries<OrganizationAuditEvent>()
            .Select(e => e.Entity)
            .ToHashSet();
        var repositoryState = db
            .ChangeTracker.Entries<Zeeq.Core.Models.CodeRepository>()
            .Where(entry => entry.Entity.OrganizationId == organizationId)
            .Select(entry =>
                (
                    Entry: entry,
                    Current: entry.Property(row => row.LibraryIds).CurrentValue,
                    Original: entry.Property(row => row.LibraryIds).OriginalValue,
                    Modified: entry.Property(row => row.LibraryIds).IsModified
                )
            )
            .ToArray();
        try
        {
            // A parent lock prevents new FK-referencing documents and establishes the lock order
            // shared by repository mapping changes. Existing children are locked before deletion.
            var library = (
                await db
                    .Libraries.FromSql(
                        $"""
                        SELECT * FROM zeeq.docs_libraries
                        WHERE organization_id = {organizationId} AND id = {libraryId} FOR UPDATE
                        """
                    )
                    .AsNoTracking()
                    .ToArrayAsync(ct)
            ).SingleOrDefault();
            if (library is null)
            {
                if (ambient is not null)
                    await ambient.ReleaseSavepointAsync(savepoint, ct);
                await CommitAsync(transaction, ct);
                return;
            }

            var documentCount = 0;
            while (true)
            {
                var documents = await db
                    .LibraryDocuments.FromSql(
                        $"""
                        SELECT * FROM zeeq.docs_library_documents
                        WHERE organization_id = {organizationId} AND library_id = {libraryId}
                        ORDER BY id LIMIT 200 FOR UPDATE
                        """
                    )
                    .AsNoTracking()
                    .ToArrayAsync(ct);
                if (documents.Length == 0)
                    break;
                var ids = documents.Select(document => document.Id).ToArray();
                var deleted = await db
                    .LibraryDocuments.Where(document =>
                        document.OrganizationId == organizationId
                        && document.LibraryId == libraryId
                        && ids.Contains(document.Id)
                    )
                    .ExecuteDeleteAsync(ct);
                documentCount += deleted;
                foreach (var document in documents)
                {
                    var sourced =
                        library.SourceKind is not null
                        || library.PublicSourceId is not null
                        || document.SyncRunId is not null
                        || document.SourceExternalId is not null;
                    audit.AppendOrganization(
                        organizationId,
                        document.TeamId,
                        LibraryDocumentAudit.Local(
                            LibraryDocumentAudit.Deleted,
                            AuditChangeType.Deleted,
                            AuditArea.Content,
                            document,
                            document.Path,
                            SnapshotForAudit(document, sourced),
                            null,
                            [],
                            document.ContentHash,
                            null
                        )
                    );
                }
                await db.SaveChangesAsync(ct);
                foreach (
                    var entry in db
                        .ChangeTracker.Entries<OrganizationAuditEvent>()
                        .Where(e => !existingFacts.Contains(e.Entity))
                        .ToArray()
                )
                    entry.State = EntityState.Detached;
            }
            // Keep only refreshed links for repositories already tracked by the caller; all
            // other rows and their facts are handled in bounded batches under the same transaction.
            var trackedIds = db
                .ChangeTracker.Entries<Zeeq.Core.Models.CodeRepository>()
                .Where(entry => entry.Entity.OrganizationId == organizationId)
                .Select(entry => entry.Entity.Id)
                .ToHashSet(StringComparer.Ordinal);
            var refreshedLinks = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var mappingCount = 0;
            while (true)
            {
                var batch = await db
                    .CodeRepositories.FromSql(
                        $"""
                        SELECT * FROM zeeq.code_review_repositories
                        WHERE organization_id = {organizationId} AND {libraryId} = ANY(library_ids)
                        ORDER BY id LIMIT 200 FOR UPDATE
                        """
                    )
                    .AsNoTracking()
                    .ToArrayAsync(ct);
                if (batch.Length == 0)
                    break;
                foreach (var repository in batch)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        UPDATE zeeq.code_review_repositories SET library_ids = array_remove(library_ids, {libraryId})
                        WHERE organization_id = {organizationId} AND id = {repository.Id}
                        """,
                        ct
                    );
                    audit.AppendOrganization(
                        organizationId,
                        library.TeamId,
                        LibraryAudit.Mapping(library, repository, mapped: false)
                    );
                    if (trackedIds.Contains(repository.Id))
                        refreshedLinks[repository.Id] = repository
                            .LibraryIds.Where(id => id != libraryId)
                            .ToArray();
                }
                mappingCount += batch.Length;
                await db.SaveChangesAsync(ct);
                foreach (
                    var entry in db
                        .ChangeTracker.Entries<OrganizationAuditEvent>()
                        .Where(e => !existingFacts.Contains(e.Entity))
                        .ToArray()
                )
                    entry.State = EntityState.Detached;
            }

            audit.AppendOrganization(
                organizationId,
                library.TeamId,
                LibraryAudit.Change(
                    "library.deleted",
                    AuditChangeType.Deleted,
                    library,
                    LibraryAudit.Snapshot(library, documentCount, mappingCount),
                    null,
                    []
                )
            );
            // Repository prompt configuration has no library/document FK and historically survives
            // deletion; its cleanup is a separate product decision, not a child cascade here.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE FROM zeeq.docs_libraries WHERE organization_id = {organizationId} AND id = {libraryId}
                """,
                ct
            );
            await db.SaveChangesAsync(ct);
            foreach (
                var entry in db
                    .ChangeTracker.Entries<Zeeq.Core.Models.CodeRepository>()
                    .Where(e =>
                        e.Entity.OrganizationId == organizationId
                        && refreshedLinks.ContainsKey(e.Entity.Id)
                    )
                    .ToArray()
            )
            {
                var current = refreshedLinks[entry.Entity.Id];
                entry.Property(e => e.LibraryIds).CurrentValue = current;
                entry.Property(e => e.LibraryIds).OriginalValue = current;
                entry.Property(e => e.LibraryIds).IsModified = false;
            }
            ct.ThrowIfCancellationRequested();
            if (ambient is not null)
                await ambient.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            await CommitAsync(transaction, ct);
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
                foreach (var state in repositoryState)
                {
                    state.Entry.Property(row => row.LibraryIds).CurrentValue = state.Current;
                    state.Entry.Property(row => row.LibraryIds).OriginalValue = state.Original;
                    state.Entry.Property(row => row.LibraryIds).IsModified = state.Modified;
                }
                foreach (
                    var entry in db
                        .ChangeTracker.Entries<OrganizationAuditEvent>()
                        .Where(e => !existingFacts.Contains(e.Entity))
                        .ToArray()
                )
                    entry.State = EntityState.Detached;
            }
            throw;
        }
        // Database deletion is complete; remove only the caller's now-deleted tracked objects.
        foreach (
            var entry in db
                .ChangeTracker.Entries<LibraryDocument>()
                .Where(e =>
                    e.Entity.OrganizationId == organizationId && e.Entity.LibraryId == libraryId
                )
                .ToArray()
        )
            entry.State = EntityState.Detached;
        foreach (
            var entry in db
                .ChangeTracker.Entries<Library>()
                .Where(e => e.Entity.OrganizationId == organizationId && e.Entity.Id == libraryId)
                .ToArray()
        )
            entry.State = EntityState.Detached;
    }
}
