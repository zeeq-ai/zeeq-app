using DotNet.Testcontainers.Builders;
using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;
using Zeeq.Data.Postgres;

namespace Zeeq.Testing;

/// <summary>
/// Database fixture that initializes a PostgreSQL test container for testing.
/// </summary>
/// <remarks>
/// It is also possible to use the Aspire runtime, but this is likely faster to
/// start up compared to Aspire. Downside: need to shut down the container manually.
/// </remarks>
public class PgDatabaseFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// This is used in the GitHub workflow when used with a service instance of
    /// Postgres
    /// </summary>
    private const string ConnectionStringEnvironmentVariable =
        "ZEEQ_TEST_POSTGRES_CONNECTION_STRING";

    private const string PostgresImageEnvironmentVariable = "ZEEQ_TEST_POSTGRES_IMAGE";

    private const string DefaultPostgresImage = "ghcr.io/zeeq-ai/zeeq-postgres:pg18";

    // Keep this name stable so every test process contends for the same
    // distributed migration lock when callers opt into one shared service
    // database through ZEEQ_TEST_POSTGRES_CONNECTION_STRING.
    private const string MigrationLockName = "zeeq:test:migrations";
    private const string SearchPath = "zeeq,public";

    private PostgreSqlContainer? _postgreSqlContainer;

    private PooledDbContextFactory<PostgresDbContext>? _contextFactory;

    private string? _connectionString;

    /// <summary>
    /// Get the connection string to the underlying container.
    /// </summary>
    public string ConnectionString => _connectionString ?? string.Empty;

    /// <summary>
    /// Creates a new `PostgresContext` for interacting with the test database.
    /// </summary>
    /// <returns>A new `PostgresContext` instance (caller to dispose).</returns>
    public PostgresDbContext CreateContext() => _contextFactory!.CreateDbContext();

    /// <summary>
    /// Initializes the PostgreSQL test container.
    /// </summary>
    /// <returns>An awaitable task.</returns>
    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable
        );

        if (string.IsNullOrEmpty(connectionString))
        {
            connectionString = await StartPostgresContainerAsync();
        }

        _connectionString = WithSearchPath(connectionString);
        _contextFactory = new PooledDbContextFactory<PostgresDbContext>(
            new DbContextOptionsBuilder<PostgresDbContext>()
                .UseNpgsql(
                    _connectionString,
                    npgsqlOptions =>
                    {
                        npgsqlOptions.MigrationsAssembly("Zeeq.Data.Postgres.Migrations");
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "zeeq");
                        npgsqlOptions.SetPostgresVersion(18, 0);
                        npgsqlOptions.UseVector();
                    }
                )
                .UseOpenIddict()
                .UseSnakeCaseNamingConvention()
                .EnableDetailedErrors(true)
                .EnableSensitiveDataLogging(true)
                .Options
        );

        using var context = _contextFactory.CreateDbContext();

        // Most tests use isolated Testcontainers databases. When callers opt
        // into a shared database with ZEEQ_TEST_POSTGRES_CONNECTION_STRING,
        // hold a transaction-scoped advisory lock while MigrateAsync runs so
        // PgBouncer transaction pooling cannot break lock ownership the way a
        // session-scoped lock can.
        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();

        await PostgresDistributedLock.AcquireWithTransactionAsync(
            new PostgresAdvisoryLockKey(MigrationLockName, allowHashing: true),
            lockTransaction,
            timeout: null
        );

        await context.Database.MigrateAsync();
        await lockTransaction.CommitAsync();
    }

    private async Task<string> StartPostgresContainerAsync()
    {
        var image =
            Environment.GetEnvironmentVariable(PostgresImageEnvironmentVariable)
            ?? DefaultPostgresImage;

        _postgreSqlContainer = new PostgreSqlBuilder(image)
            .WithPortBinding(5432, true)
            .WithPassword("password")
            .WithUsername("username")
            // The custom image sets pg_cron's cron.database_name to zeeq.
            // Keep local Testcontainers aligned with CI so migrations can
            // create pg_cron in the expected database.
            .WithDatabase("zeeq")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilMessageIsLogged("database system is ready to accept connections")
                    .UntilInternalTcpPortIsAvailable(5432)
            )
            .WithAutoRemove(true)
            // .WithReuse(true)
            .Build();

        await _postgreSqlContainer.StartAsync();

        _connectionString = WithSearchPath(_postgreSqlContainer.GetConnectionString());

        return _connectionString;
    }

    private static string WithSearchPath(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = SearchPath,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Cleans up the container.
    /// </summary>
    /// <returns>An awaitable task.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_postgreSqlContainer != null)
        {
            if (_contextFactory != null)
            {
                using var context = _contextFactory.CreateDbContext();
                await context.Database.EnsureDeletedAsync();
            }

            await _postgreSqlContainer.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

internal class PooledDbConnectionFactory { }
