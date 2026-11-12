using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Owns a store's transaction only when the caller has not already opened one.
/// </summary>
/// <remarks>
/// Production callers rely on these stores owning their transaction and committing it. Tests
/// and any future composition that wraps a store in an ambient transaction must keep the
/// outer transaction authoritative: in that case this helper joins it and never commits or
/// rolls it back, so the outer owner decides the outcome. Error paths in the evaluation
/// stores make no writes before they decide to abort, so the no-op rollback cannot leave a
/// partial draft inside the caller's transaction.
/// </remarks>
internal sealed class ScopedStoreTransaction : IAsyncDisposable
{
    private readonly IDbContextTransaction? _owned;

    private ScopedStoreTransaction(IDbContextTransaction transaction, bool owns)
    {
        Transaction = transaction;
        _owned = owns ? transaction : null;
    }

    /// <summary>Transaction the store must use for its SQL and advisory locks.</summary>
    public IDbContextTransaction Transaction { get; }

    /// <summary>True when this helper opened and therefore must finish the transaction.</summary>
    public bool OwnsTransaction => _owned is not null;

    /// <summary>Joins the ambient transaction or opens one.</summary>
    public static async Task<ScopedStoreTransaction> BeginAsync(
        PostgresDbContext db,
        CancellationToken cancellationToken
    )
    {
        if (db.Database.CurrentTransaction is { } ambient)
        {
            return new(ambient, owns: false);
        }

        return new(await db.Database.BeginTransactionAsync(cancellationToken), owns: true);
    }

    /// <summary>Commits when this helper owns the transaction.</summary>
    public Task CommitAsync(CancellationToken cancellationToken) =>
        _owned?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Rolls back when this helper owns the transaction; otherwise does nothing.</summary>
    public Task RollbackAsync(CancellationToken cancellationToken) =>
        _owned?.RollbackAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// Narrows database update failures to specific PostgreSQL constraint violations.
/// </summary>
internal static class PostgresConstraintViolation
{
    /// <summary>
    /// True when the failure is a unique violation on the named index.
    /// </summary>
    /// <remarks>
    /// PostgreSQL truncates identifiers to 63 bytes, so the comparison accepts a prefix match
    /// against the reported constraint name.
    /// </remarks>
    public static bool IsUniqueViolation(DbUpdateException exception, string constraintPrefix) =>
        exception.InnerException
            is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: { } constraintName,
            }
        && constraintName.StartsWith(constraintPrefix, StringComparison.Ordinal);
}
