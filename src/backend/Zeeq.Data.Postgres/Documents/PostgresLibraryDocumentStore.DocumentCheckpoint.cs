using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

internal sealed partial class PostgresLibraryDocumentStore
{
    private PropertyValues? documentPreimage;
    private (string OrganizationId, string LibraryId, string Id) documentIdentity;

    private void RememberDocumentPreimage(LibraryDocument document)
    {
        documentIdentity = (document.OrganizationId, document.LibraryId, document.Id);
        documentPreimage = SnapshotDocumentValues(db.Entry(document).CurrentValues);
    }

    // SaveChanges can accept unrelated caller changes before a later failure. Keep their tracker
    // state until the operation commits, but reset the attempted target to its locked preimage.
    private async Task<T> RunDocumentMutationAsync<T>(
        Func<Task<T>> action,
        LibraryDocument? input,
        CancellationToken ct
    )
    {
        db.ChangeTracker.DetectChanges();
        var snapshots = db
            .ChangeTracker.Entries()
            .Select(entry => new
            {
                Entry = entry,
                entry.State,
                Current = SnapshotDocumentValues(entry.CurrentValues),
                Original = SnapshotDocumentValues(entry.OriginalValues),
                Modified = entry
                    .Properties.Where(p => p.IsModified)
                    .Select(p => p.Metadata.Name)
                    .ToArray(),
            })
            .ToArray();
        var prior = snapshots
            .Select(s => s.Entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var inputEntry = input is null
            ? null
            : snapshots.FirstOrDefault(s =>
                s.Entry.Entity is LibraryDocument tracked
                && tracked.Id == input.Id
                && tracked.OrganizationId == input.OrganizationId
                && tracked.LibraryId == input.LibraryId
            );
        documentPreimage = null;
        var ambient = db.Database.CurrentTransaction;
        IDbContextTransaction? owned = null;
        var savepoint = $"document_{Guid.NewGuid():N}";
        var checkpointCreated = false;
        try
        {
            if (ambient is null)
                owned = await db.Database.BeginTransactionAsync(ct);
            else
            {
                await ambient.CreateSavepointAsync(savepoint, ct);
                checkpointCreated = true;
            }
            Zeeq.Data.Postgres.CodeReviews.RepositoryLinkGuard.RejectPending(db);
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
                if (ambient is not null && checkpointCreated)
                    await ambient.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                else if (owned is not null)
                    await owned.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                // A rollback failure propagates: tracker cleanup cannot make that transaction reusable.
                foreach (
                    var entry in db
                        .ChangeTracker.Entries()
                        .Where(e => !prior.Contains(e.Entity))
                        .ToArray()
                )
                    entry.State = EntityState.Detached;
                foreach (var snapshot in snapshots)
                {
                    snapshot.Entry.State =
                        snapshot.State == EntityState.Detached
                            ? EntityState.Detached
                            : EntityState.Unchanged;
                    snapshot.Entry.CurrentValues.SetValues(snapshot.Current);
                    snapshot.Entry.OriginalValues.SetValues(snapshot.Original);
                    snapshot.Entry.State = snapshot.State;
                    if (snapshot.State == EntityState.Modified)
                        foreach (var property in snapshot.Entry.Properties)
                            property.IsModified = snapshot.Modified.Contains(
                                property.Metadata.Name
                            );
                }
                // Only upsert inputs carry the attempted edit. For delete, move and toggles,
                // the caller's pre-existing tracked edits are independent work.
                if (input is not null && documentPreimage is not null)
                {
                    var target = db
                        .ChangeTracker.Entries<LibraryDocument>()
                        .FirstOrDefault(e =>
                            e.Entity.Id == documentIdentity.Id
                            && e.Entity.OrganizationId == documentIdentity.OrganizationId
                            && e.Entity.LibraryId == documentIdentity.LibraryId
                        );
                    if (target is not null)
                    {
                        target.CurrentValues.SetValues(documentPreimage);
                        target.OriginalValues.SetValues(documentPreimage);
                        target.State = EntityState.Unchanged;
                    }
                }
                else if (inputEntry is not null && inputEntry.State != EntityState.Added)
                {
                    // If cancellation or locking fails before a database preimage is available,
                    // original tracked values prevent the attempted edit from being replayed.
                    inputEntry.Entry.CurrentValues.SetValues(inputEntry.Original);
                    inputEntry.Entry.OriginalValues.SetValues(inputEntry.Original);
                    inputEntry.Entry.State = EntityState.Unchanged;
                }
                else if (inputEntry is not null)
                    inputEntry.Entry.State = EntityState.Detached;
            }
            throw;
        }
        finally
        {
            documentPreimage = null;
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }

    private static PropertyValues SnapshotDocumentValues(PropertyValues values)
    {
        var copy = values.Clone();
        foreach (var property in values.Properties)
            copy[property] =
                property.GetValueComparer()?.Snapshot(values[property]) ?? values[property];
        return copy;
    }
}
