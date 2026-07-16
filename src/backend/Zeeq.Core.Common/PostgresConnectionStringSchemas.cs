using Npgsql;

namespace Zeeq.Core.Common;

/// <summary>
/// Ensures Postgres connection strings carry every schema Zeeq depends on
/// (EF Core's default schema, raw SQL, and extension-owned schemas) on the
/// <c>search_path</c>, since Postgres only searches <c>"$user", public</c> by
/// default and unqualified relation references would otherwise fail.
/// </summary>
internal static class PostgresConnectionStringSchemas
{
    /// <summary>
    /// Schemas Zeeq's EF model, raw SQL, messaging, cache, and pg_cron rely on
    /// being resolvable without a schema qualifier.
    /// </summary>
    private static readonly string[] RequiredSchemas =
    [
        "zeeq",
        "public",
        "messaging",
        "cache",
        "cron",
    ];

    /// <summary>
    /// Returns <paramref name="connectionString"/> with any missing required
    /// schemas appended to its <c>search_path</c>. Existing schemas and their
    /// order are preserved; missing ones are appended in <see cref="RequiredSchemas"/> order.
    /// </summary>
    public static string EnsureRequiredSearchPath(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var existingSchemas = string.IsNullOrWhiteSpace(builder.SearchPath)
            ? []
            : builder.SearchPath.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            );

        var missingSchemas = Array.FindAll(
            RequiredSchemas,
            schema => Array.IndexOf(existingSchemas, schema) < 0
        );

        if (missingSchemas.Length == 0)
        {
            return connectionString;
        }

        builder.SearchPath = string.Join(',', [.. existingSchemas, .. missingSchemas]);
        return builder.ConnectionString;
    }
}
