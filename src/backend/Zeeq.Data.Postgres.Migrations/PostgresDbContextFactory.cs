using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Zeeq.Data.Postgres.Migrations;

/// <summary>
/// Design-time factory so the EF Core CLI (<c>dotnet ef migrations add</c>,
/// <c>dotnet ef database update</c>) can construct <see cref="PostgresDbContext"/>
/// without the full ASP.NET dependency-injection container.
/// </summary>
/// <remarks>
/// Connection string priority:
/// <list type="number">
///   <item><c>ConnectionStrings__zeeq-db</c> — Aspire-injected (production dev)</item>
///   <item><c>ZEEQ_TEST_POSTGRES_CONNECTION_STRING</c> — local / CI testing</item>
///   <item>Hard-coded fallback for local development</item>
/// </list>
/// </remarks>
public sealed class PostgresDbContextFactory : IDesignTimeDbContextFactory<PostgresDbContext>
{
    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=zeeq;Username=zeeq;Password=P@ssw0rd;Include Error Detail=true;Log Parameters=true";

    /// <inheritdoc />
    public PostgresDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__zeeq-db")
            ?? Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            ?? FallbackConnectionString;

        // NOTE: This setup has to match the one in `PostgresSetupExtension.cs`
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                connectionString,
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
            .Options;

        return new PostgresDbContext(options);
    }
}
