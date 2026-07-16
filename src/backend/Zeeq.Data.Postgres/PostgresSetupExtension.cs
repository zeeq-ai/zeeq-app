using Zeeq.Core.Carts;
using Zeeq.Core.Common;
using Zeeq.Core.Common.Storage;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Core.Identity;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Carts;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Data.Postgres.LlmSettings;
using Zeeq.Data.Postgres.Metrics;
using Zeeq.Data.Postgres.Telemetry;
using Zeeq.Platform.CodeReviews;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zeeq.Data.Postgres;

/// <summary>
/// Sets up Postgres as the backing data store for Zeeq's EF Core data access layer
/// and provides a hook to apply pending migrations at startup.
/// </summary>
public static class PostgresSetupExtension
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PostgresSetupExtension));

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers PostgreSQL EF Core services and identity stores.
        /// </summary>
        public IServiceCollection AddPostgres(AppSettings appSettings)
        {
            var connectionString = appSettings.Database.EffectiveConnectionString;
            var csb = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);

            Log.Here()
                .Information(
                    "⚙️  Configuring Postgres. Host: {Host}; Port: {Port}; Database: {Database}; Username: {Username}; SearchPath: {SearchPath}",
                    csb.Host,
                    csb.Port,
                    csb.Database,
                    csb.Username,
                    csb.SearchPath
                );

            // Use the same options for scoped request contexts and factory-created
            // contexts. The GitHub comment lease renewal loop creates a separate
            // context while the handler's main work task continues using scoped
            // stores, because EF Core DbContext instances are not thread-safe.
            services.AddDbContext<ZeeqDbContextBase, PostgresDbContext>(
                (_, options) => ConfigurePostgresOptions(options, connectionString)
            );
            services.AddDbContextFactory<PostgresDbContext>(
                (_, options) => ConfigurePostgresOptions(options, connectionString),
                // The app also registers PostgresDbContext as a scoped request
                // context. EF Core's factory defaults to singleton, which
                // cannot consume the scoped options produced by AddDbContext.
                // The lease renewal loop only needs the factory inside scoped
                // message handling, so a scoped factory keeps validation honest.
                ServiceLifetime.Scoped
            );
            services
                .AddDataProtection()
                .SetApplicationName("Zeeq")
                .PersistKeysToDbContext<PostgresDbContext>();

            // Register Postgres-specific implementations of identity stores.
            services.AddScoped<IZeeqIdentityStore, PostgresZeeqIdentityStore>();
            services.AddScoped<IZeeqAuthStateStore, PostgresZeeqAuthStateStore>();
            services.AddScoped<IZeeqMembershipStore, PostgresZeeqMembershipStore>();
            services.AddScoped<ILlmSettingsStore, PostgresLlmSettingsStore>();
            services.AddScoped<IEncryptedValueStore, PostgresEncryptedValueStore>();
            services.AddScoped<IGitHubInstallationStore, PostgresGitHubInstallationStore>();
            services.AddScoped<ICodeRepositoryStore, PostgresCodeRepositoryStore>();
            services.AddScoped<IPullRequestRecordStore, PostgresPullRequestRecordStore>();
            services.AddScoped<IPullRequestLookupStore, PostgresPullRequestLookupStore>();
            services.AddScoped<ICodeReviewRecordStore, PostgresCodeReviewRecordStore>();
            services.AddScoped<IMetricEventStore, PostgresMetricEventStore>();
            services.AddScoped<ITelemetryRawRequestStore, PostgresTelemetryRawRequestStore>();
            services.AddScoped<IAgentTelemetryDomainStore, PostgresAgentTelemetryDomainStore>();
            services.AddScoped<IMetricsQueryStore, PostgresMetricsQueryStore>();
            services.AddScoped<
                ICodeReviewOrganizationSettingsStore,
                PostgresCodeReviewOrganizationSettingsStore
            >();
            services.AddScoped<
                ICodeReviewExecutionLeaseStore,
                PostgresCodeReviewExecutionLeaseStore
            >();
            services.AddScoped<ICodeReviewerAgentStore, PostgresCodeReviewerAgentStore>();
            services.AddScoped<IActiveCodeReviewLockStore, PostgresActiveCodeReviewLockStore>();
            services.AddScoped<
                IStorageProvider<PostgresStorageWriteOptions>,
                PostgresStorageProvider
            >();
            services.AddScoped<ICodeReviewArtifactStore, PostgresCodeReviewArtifactStore>();
            services.AddScoped<ICodeReviewPreviousReviewStore, CodeReviewPreviousReviewStore>();
            services.AddScoped<IGitHubWebhookDeliveryStore, PostgresGitHubWebhookDeliveryStore>();
            services.AddScoped<IGitHubCommentLeaseStore, PostgresGitHubCommentLeaseStore>();
            services.AddScoped<IGitHubCommentAnchorStore, PostgresGitHubCommentAnchorStore>();
            services.AddScoped<ICartStore, PostgresCartStore>();
            services.AddScoped<LibraryDocumentWriteService>();

            // Per-scope query context read by the document/snippet stores. Default is unmarked
            // (no filtering); only the code-review tool path (ScopedServiceAIFunction) marks it,
            // hiding ExcludedFromCodeReviews documents from list/search on that path alone.
            services.TryAddScoped<DocumentSearchScope>();

            services.AddScoped<PostgresLibraryDocumentStore>();
            services.AddScoped<ILibraryDocumentStore>(serviceProvider =>
            {
                var store = serviceProvider.GetRequiredService<PostgresLibraryDocumentStore>();
                var cache = serviceProvider.GetService<HybridCache>();

                return cache is null ? store : new CachedLibraryDocumentStore(store, cache);
            });
            services.AddScoped<IDocsPublicSourceStore, PostgresDocsPublicSourceStore>();
            services.AddScoped<IDocsPublicDocumentStore, PostgresDocsPublicDocumentStore>();
            services.AddScoped<IDocsIngestRunStore, PostgresDocsIngestRunStore>();
            services.AddScoped<
                ISnippetStore<LibraryDocument>,
                PostgresLibraryDocumentSnippetStore
            >();
            services.AddScoped<
                ISnippetStore<DocsPublicDocument>,
                PostgresPublicDocumentSnippetStore
            >();

            return services;
        }
    }

    /// <summary>
    /// Applies the shared Postgres EF options used by scoped and factory-created contexts.
    /// </summary>
    private static void ConfigurePostgresOptions(
        DbContextOptionsBuilder options,
        string connectionString
    )
    {
        options
            .UseNpgsql(
                connectionString,
                npgsqlOptions =>
                {
                    // Runtime references the migrations assembly for now so local/dev
                    // migration application can use the same host. We may want to
                    // decouple runtime and migrations later with a design/deploy-time
                    // migration runner.
                    npgsqlOptions.MigrationsAssembly("Zeeq.Data.Postgres.Migrations");
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "zeeq");
                    npgsqlOptions.SetPostgresVersion(18, 0);

                    // Enable pgvector type mapping (halfvec/vector columns on snippet tables)
                    // for both scoped-request and factory-created contexts.
                    npgsqlOptions.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .EnableDetailedErrors(true)
            .EnableSensitiveDataLogging(true);
    }

    /// <summary>
    /// Applies any pending EF Core migrations to the Postgres database.
    /// Called once at startup after the DI container is built.
    /// </summary>
    extension(IServiceProvider services)
    {
        /// <summary>
        /// Runs a migration using the Postgres database context.
        /// </summary>
        public async Task UsePostgresAsync()
        {
            using var scope = services.CreateScope();

            var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();

            await db.Database.MigrateAsync();

            Log.Here().Information("✅  Postgres database migrations applied successfully");
        }
    }
}
