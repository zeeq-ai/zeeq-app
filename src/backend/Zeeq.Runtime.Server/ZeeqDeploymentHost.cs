using System.Reflection;
using System.Text.Json;
using Npgsql;
using Paramore.Brighter;
using Zeeq.Core.Llm;
using Zeeq.Integrations.Notion;
using Zeeq.Platform.CodeReviews;
using Zeeq.Platform.Ingest;
using Zeeq.Platform.Membership;
using Zeeq.Platform.Messaging;
using Zeeq.Platform.Messaging.AwsSqs;

namespace Zeeq.Runtime.Server;

/// <summary>Runs installation operations without HTTP, messaging, or tenant encryption services.</summary>
internal static class ZeeqDeploymentHost
{
    internal static async Task RunAsync(string[] args, ZeeqRunMode mode)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddZeeqConfigJson();
        var settings =
            builder.Configuration.GetSection(nameof(AppSettings)).Get<AppSettings>() ?? new();
        if (mode == ZeeqRunMode.Topology)
        {
            var options =
                builder.Configuration.GetSection("ZeeqMessaging").Get<ZeeqMessagingOptions>()
                ?? new();
            var sqs =
                builder
                    .Configuration.GetSection(AwsSqsMessagingOptions.SectionName)
                    .Get<AwsSqsMessagingOptions>()
                ?? throw new InvalidOperationException(
                    "SQS settings required for topology export."
                );
            var catalog = new MessagingCatalogScanner().Scan(
                Assembly.GetExecutingAssembly(),
                typeof(OrganizationEndpoints).Assembly,
                typeof(SetupCodeReviews).Assembly,
                typeof(SetupMcpExtensions).Assembly,
                typeof(SetupZeeqIngest).Assembly,
                typeof(SetupNotionIntegration).Assembly,
                typeof(Zeeq.Platform.Metrics.SetupZeeqMetrics).Assembly
            );
            var topology = new AwsSqsMessagingTopology(catalog, options, sqs);
            Console.WriteLine(
                JsonSerializer.Serialize(
                    topology.Publications.Select(p => new
                    {
                        name = p.ChannelName!.Value,
                        visibilityTimeout = (int)p.QueueAttributes.LockTimeout.TotalSeconds,
                        route = p.Topic!.Value,
                    })
                )
            );
            return;
        }
        builder.AddZeeqLogging();
        if (mode == ZeeqRunMode.VerifyInfrastructure)
        {
            await AwsInstallationVerification.RunAsync(settings, builder.Configuration);
            return;
        }
        if (mode == ZeeqRunMode.DatabaseCleanup)
        {
            using var cleanupConnection = new NpgsqlConnection(
                settings.Database.EffectiveConnectionString
            );
            await cleanupConnection.OpenAsync();
            await using var cleanupCommand = cleanupConnection.CreateCommand();
            cleanupCommand.CommandText = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Deployment", "cleanup.sql")
            );
            await cleanupCommand.ExecuteNonQueryAsync();
            Console.WriteLine(
                "Installation SQL objects removed from the reused database; external roles and extensions retained."
            );
            return;
        }
        if (mode == ZeeqRunMode.DatabaseBootstrap)
        {
            await BootstrapAsync(settings, builder.Configuration);
            return;
        }

        // Validate the effective server identity before EF can create objects or schedule cron jobs.
        await using var connection = new NpgsqlConnection(
            settings.Database.EffectiveConnectionString
        );
        await connection.OpenAsync();
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = "SELECT current_user";
            if (
                !string.Equals(
                    await identity.ExecuteScalarAsync() as string,
                    "zeeq_owner",
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    "Migrate requires the persistent zeeq_owner identity."
                );
            }
        }

        builder.Services.AddSingleton(settings);
        builder.Services.AddZeeqData(settings);
        using var host = builder.Build();
        await host.Services.UsePostgresAsync();
        // Cache DDL is intentionally owner-only, outside EF migrations. Match the pinned library.
        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Deployment", "cache.sql")
        );
        await command.ExecuteNonQueryAsync();
        Console.WriteLine("Database migrations and cache preparation complete.");
    }

    private static async Task BootstrapAsync(AppSettings settings, IConfiguration config)
    {
        var ownerPassword =
            config["ZeeqDeployment:OwnerPassword"]
            ?? throw new InvalidOperationException("Owner password required.");
        var runtimePassword =
            config["ZeeqDeployment:RuntimePassword"]
            ?? throw new InvalidOperationException("Runtime password required.");
        var master = new NpgsqlConnectionStringBuilder(settings.Database.EffectiveConnectionString);
        if (config["ZeeqDeployment:MasterPassword"] is { } masterPassword)
        {
            master.Password = masterPassword;
        }
        using var connection = new NpgsqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        // Advisory locking serializes retries. The SQL validates the existing identity contract.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_lock(726331);";
        await command.ExecuteNonQueryAsync();
        command.CommandText = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Deployment", "bootstrap.sql")
        );
        await command.ExecuteNonQueryAsync();
        // ALTER ROLE cannot parameterize a password. Quote with the server's quote_literal; do not log it.
        foreach (
            var (role, password) in new[]
            {
                ("zeeq_owner", ownerPassword),
                ("zeeq_runtime", runtimePassword),
            }
        )
        {
            command.CommandText = "SELECT quote_literal(@password)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("password", password);
            var literal = (string)(await command.ExecuteScalarAsync())!;
            command.Parameters.Clear();
            command.CommandText = $"ALTER ROLE {role} LOGIN PASSWORD {literal}";
            await command.ExecuteNonQueryAsync();
        }
        Console.WriteLine("Database roles, schemas, extensions and grants prepared.");
    }
}
