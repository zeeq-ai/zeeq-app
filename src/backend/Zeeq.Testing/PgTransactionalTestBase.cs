using System.Data;
using Zeeq.Data.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Zeeq.Testing;

/// <summary>
/// Base class for PostgreSQL tests that require transactional isolation.
/// Each test gets its own <see cref="PostgresDbContext"/> and an explicit EF Core
/// transaction that is always rolled back in <see cref="Cleanup"/>, ensuring
/// database state is never persisted between tests.
/// </summary>
public abstract class PgTransactionalTestBase(PgDatabaseFixture postgres)
{
    private IDbContextTransaction? _transaction;

    /// <summary>
    /// EF Core context used by the current transactional test.
    /// </summary>
    protected PostgresDbContext _context = postgres.CreateContext();

    /// <summary>
    /// Before test hook that starts a stable-snapshot transaction on the context.
    /// </summary>
    /// <remarks>
    /// <see cref="IsolationLevel.RepeatableRead" /> keeps parallel tests from
    /// observing rows committed by other tests between assertions.
    /// </remarks>
    [Before(Test)]
    public async Task Before()
    {
        _transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
    }

    /// <summary>
    /// After test hook that rolls back the transaction and disposes the context.
    /// </summary>
    [After(Test)]
    public async Task Cleanup()
    {
        if (_transaction != null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }

        await _context.DisposeAsync();
    }
}
