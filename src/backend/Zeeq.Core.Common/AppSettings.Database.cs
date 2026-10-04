using System.Collections.Concurrent;

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
    // Record copies can share this cache safely: normalization is keyed by input, never by captured this.
    private readonly ConcurrentDictionary<string, string> _normalizedConnectionStrings = new(
        StringComparer.Ordinal
    );

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
    public string EffectiveConnectionString =>
        _normalizedConnectionStrings.GetOrAdd(
            ConnectionString,
            PostgresConnectionStringSchemas.EnsureRequiredSearchPath
        );

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
    public string EffectiveWorkerConnectionString =>
        _normalizedConnectionStrings.GetOrAdd(
            string.IsNullOrWhiteSpace(WorkerConnectionString)
                ? ConnectionString
                : WorkerConnectionString,
            PostgresConnectionStringSchemas.EnsureRequiredSearchPath
        );

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
    public string EffectiveCacheConnectionString =>
        _normalizedConnectionStrings.GetOrAdd(
            string.IsNullOrWhiteSpace(CacheConnectionString)
                ? ConnectionString
                : CacheConnectionString,
            PostgresConnectionStringSchemas.EnsureRequiredSearchPath
        );

    /// <summary>Run EF migrations at startup. Disable when an installation runs its own migration task.</summary>
    public bool MigrateOnStartup { get; init; } = true;

    /// <summary>Database provider.</summary>
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
