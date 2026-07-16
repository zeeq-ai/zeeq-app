using Npgsql;

namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Database related configuration settings.
    /// </summary>
    public DatabaseSettings Database { get; init; } = new();
}

/// <summary>
/// The database settings for initializing the database connection.
/// </summary>
public record DatabaseSettings
{
    private readonly Lazy<string> _effectiveConnectionString;
    private readonly Lazy<string> _effectiveWorkerConnectionString;
    private readonly Lazy<string> _effectiveCacheConnectionString;

    /// <summary>
    /// Builds the lazily-evaluated, search_path-augmented connection strings.
    /// </summary>
    /// <remarks>
    /// The `Lazy&lt;string&gt;` factories close over `this`, not the property values
    /// at construction time, so they see whatever `init` values configuration
    /// binding ultimately applies and then parse each connection string via
    /// <see cref="NpgsqlConnectionStringBuilder"/> only once, on first access.
    /// </remarks>
    public DatabaseSettings()
    {
        _effectiveConnectionString = new Lazy<string>(() =>
            PostgresConnectionStringSchemas.EnsureRequiredSearchPath(ConnectionString)
        );
        _effectiveWorkerConnectionString = new Lazy<string>(() =>
            PostgresConnectionStringSchemas.EnsureRequiredSearchPath(
                string.IsNullOrWhiteSpace(WorkerConnectionString)
                    ? ConnectionString
                    : WorkerConnectionString
            )
        );
        _effectiveCacheConnectionString = new Lazy<string>(() =>
            PostgresConnectionStringSchemas.EnsureRequiredSearchPath(
                string.IsNullOrWhiteSpace(CacheConnectionString)
                    ? ConnectionString
                    : CacheConnectionString
            )
        );
    }

    /// <summary>
    /// The connection string used to connect to the underlying database.
    /// </summary>
    /// <remarks>
    /// Map Aspire-injected connection string (ConnectionStrings__zeeq-db) into the
    /// AppSettings config section so that both the runtime binding below and the
    /// IOptions{AppSettings} registered for DI pick it up automatically.
    /// </remarks>
    public string ConnectionString { get; init; } =
        Environment.GetEnvironmentVariable("ConnectionStrings__zeeq-db")
        ?? Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
        ?? string.Empty;

    /// <summary>
    /// <see cref="ConnectionString"/> with any Zeeq-required schemas
    /// (zeeq, public, messaging, cache, cron) added to the search_path if missing.
    /// Use this, not <see cref="ConnectionString"/>, when opening a connection.
    /// </summary>
    public string EffectiveConnectionString => _effectiveConnectionString.Value;

    /// <summary>
    /// Connection string used by the standalone worker process.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ConnectionString" /> at runtime when not configured.
    /// Keeping this separate lets production tune worker connection pools without
    /// changing the web service connection string.
    ///
    /// Set as: `AppSettings__Database__WorkerConnectionString`
    /// </remarks>
    public string WorkerConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Effective worker connection string after applying the main connection fallback
    /// and ensuring required schemas are present on the search_path.
    /// </summary>
    public string EffectiveWorkerConnectionString => _effectiveWorkerConnectionString.Value;

    /// <summary>
    /// Connection string for the distributed cache provider.
    /// Defaults to the main database connection string if not separately configured.
    /// For Postgres: a standard Npgsql connection string.
    /// For Redis: a StackExchange.Redis connection string (e.g., "localhost:6379").
    /// </summary>
    public string CacheConnectionString { get; init; } =
        Environment.GetEnvironmentVariable("ConnectionStrings__zeeq-cache")
        ?? Environment.GetEnvironmentVariable("ConnectionStrings__zeeq-db")
        ?? string.Empty;

    /// <summary>
    /// Effective cache connection string after applying the main connection fallback
    /// and ensuring required schemas are present on the search_path.
    /// </summary>
    public string EffectiveCacheConnectionString => _effectiveCacheConnectionString.Value;

    /// <summary>
    /// The database provider to use for the application. This determines which
    /// EF Core provider to use and how to configure the database context.
    /// </summary>
    public DatabaseProvider Provider { get; init; } = DatabaseProvider.Postgres;
}

/// <summary>
/// The supported database providers for the application. This is used to
/// determine which EF Core provider to use and how to configure the database
/// context.
/// </summary>
public enum DatabaseProvider
{
    /// <summary>
    /// PostgreSQL database provider.
    /// </summary>
    Postgres = 0, // Default and only for now.
}
