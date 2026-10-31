using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Zeeq.Data.Postgres.CodeReviews;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>Pairs a security mutation with its explicit audit append, including caught failures.</summary>
/// <remarks>
/// A database savepoint does not restore EF's tracker. A failed append or save must also remove
/// new facts and restore pending caller state, or a later SaveChanges can replay the failed work.
/// This helper does not flush before its checkpoint and never commits a caller-owned transaction.
/// Producers that accept pre-mutated tracked input also need authoritative target recovery.
/// </remarks>
internal static class AuditMutationCheckpoint
{
    internal static async Task<T> RunAsync<T>(
        PostgresDbContext db,
        Func<Task<T>> mutation,
        CancellationToken ct
    )
    {
        // Saving this boundary also flushes the shared context. Repository link arrays require
        // their own audited mapping boundary; do not persist a pending array as incidental work.
        RepositoryLinkGuard.RejectPending(db);
        var ambient = db.Database.CurrentTransaction;
        await using var owned = ambient is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var savepoint = $"audit_mutation_{Guid.NewGuid():N}";
        if (ambient is not null)
            await ambient.CreateSavepointAsync(savepoint, ct);
        var snapshots = db
            .ChangeTracker.Entries()
            .Select(entry => new
            {
                Entry = entry,
                entry.State,
                Current = Snapshot(entry.CurrentValues),
                Original = Snapshot(entry.OriginalValues),
                Modified = entry
                    .Properties.Where(property => property.IsModified)
                    .Select(property => property.Metadata.Name)
                    .ToHashSet(),
            })
            .ToArray();
        var prior = snapshots
            .Select(snapshot => snapshot.Entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        try
        {
            var result = await mutation();
            ct.ThrowIfCancellationRequested();
            if (owned is not null)
                await owned.CommitAsync(ct);
            else
                await ambient!.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            return result;
        }
        catch
        {
            try
            {
                if (owned is not null)
                    await owned.RollbackAsync(CancellationToken.None);
                else
                    await ambient!.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            }
            finally
            {
                // Cleanup does not make a failed rollback safe. Its exception propagates;
                // the caller must discard that context and its ambient transaction.
                foreach (
                    var entry in db
                        .ChangeTracker.Entries()
                        .Where(entry => !prior.Contains(entry.Entity))
                        .ToArray()
                )
                    entry.State = EntityState.Detached;
                // SaveChanges can accept unrelated pending entries before a subsequent failure.
                // Restore their pre-call values and flags so the caller can save them again.
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

    private static PropertyValues Snapshot(PropertyValues values)
    {
        var snapshot = values.Clone();
        foreach (var property in values.Properties)
            snapshot[property] =
                property.GetValueComparer()?.Snapshot(values[property]) ?? values[property];
        return snapshot;
    }
}
