using System.Reflection;
using Scalar.AspNetCore;
using Zeeq.Core.Common.AspNetCore;
using Zeeq.Core.Common.AspNetCore.Endpoints;
using Zeeq.Core.Llm;
using Zeeq.Integrations.GitHub;
using Zeeq.Platform.CodeReviews;
using Zeeq.Platform.Dispatch.Process;
using Zeeq.Platform.Documents;
using Zeeq.Platform.Ingest;
using Zeeq.Platform.Llm;
using Zeeq.Platform.Metrics;
using Zeeq.Platform.Storage.Google;
using Zeeq.Platform.Telemetry.Setup;
using Zeeq.Runtime.Server;

// If the entry point is the OpenAPI generation, we can skip everything else.
HandleSchemaGenerationAndExit();

if (ZeeqRuntimeMode.Current == ZeeqRunMode.Worker)
{
    await ZeeqWorkerHost.RunAsync(args);

    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddZeeqLogging();

Log.Information(
    "zeeq-runtime {Version} ({Sha}) built {BuildTimeUtc} UTC ({BuildTimeEst})",
    GitVersionInfo.DisplayVersion,
    GitVersionInfo.Sha ?? "unknown",
    GitVersionInfo.BuildTimeUtc?.ToString("o"),
    GitVersionInfo.BuildTimeEst ?? "unknown"
);

var appSettings =
    builder.Configuration.GetSection(nameof(AppSettings)).Get<AppSettings>()
    ?? throw new InvalidOperationException("AppSettings configuration is required.");

builder
    .Services.AddOptions<AppSettings>()
    .Bind(builder.Configuration.GetSection(nameof(AppSettings)))
    .ValidateOnStart();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder
    .Services.AddRequestDecompression()
    .AddExceptionHandler<ZeeqHttpExceptionHandler>()
    .AddZeeqTelemetry()
    .AddZeeqOpenApiConfig()
    .AddZeeqJsonConfig()
    .AddZeeqForwardedHeadersConfig()
    .AddZeeqData(appSettings)
    .AddZeeqCache(appSettings)
    .AddZeeqMessaging(appSettings, builder.Configuration, ZeeqRuntimeMode.MessagingRole)
    .AddZeeqGitHubIntegration(appSettings)
    .AddZeeqLlm(appSettings.Llm, builder.Environment)
    .AddZeeqLlmPlatform()
    .AddGoogleKmsDataEncryption(appSettings.Llm)
    .AddZeeqCodeReviews(appSettings.CodeReview)
    .AddZeeqIngest(appSettings)
    // Registered here too, not just in ZeeqWorkerHost: ZEEQ_MESSAGING_ROLE
    // (Producer/Consumer/ProducerConsumer) is independent of this process's
    // Web/Worker shape (ZEEQ_RUN_MODE) — this web-mode process can still be
    // configured as a Consumer or ProducerConsumer (local dev runs it as
    // ProducerConsumer in one process), in which case Brighter will actually
    // dispatch PublicRepositorySyncRequested here and needs
    // IRepositoryIngestDispatcher resolvable. Unlike IngestSchedulerHostedService
    // (a BackgroundService that starts polling unconditionally the moment it's
    // registered), this registration is only ever exercised when the
    // configured messaging role includes Consumer — it is not dead wiring.
    .AddZeeqDispatchProcess()
    .AddZeeqIdentity<PostgresDbContext>(appSettings, builder.Configuration, builder.Environment)
    .AddZeeqMcp(builder.Environment)
    .AddZeeqDocuments(appSettings)
    // Metrics capture pipeline (MeterListener → channel → Brighter batch). Registered
    // in both hosts: web and worker are both metric producers. The batch consumer runs
    // only where the messaging role includes Consumer.
    .AddZeeqMetrics()
    .AddTelemetryIngest(appSettings.Telemetry)
    .AddZeeqEndpoints()
    .AddValidation();

LlmStartupDiagnostics.LogEmbeddingConfigurationStatus(appSettings.Llm);

// The snippet indexing sweep is a live BackgroundService. It normally runs worker-only
// (ZeeqWorkerHost), but the local Aspire topology is a single web-mode process with no
// separate worker resource — so register it here in Development only, so local dev actually
// indexes snippets. This mirrors the ingest scheduler's known worker-only gap, deliberately
// closed for this feature because local testing needs the sweep live.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddZeeqSnippetIndexing();
    builder.Services.AddZeeqStalledIngestSyncCleanup();
}

var app = builder.Build();

// This will trigger migrations to run.
await app.Services.UseZeeqDataAsync(appSettings);

await app.Services.SeedMcpIdentityAsync();

// Force eager creation of the cache schema and table.
await app.Services.UseZeeqCacheAsync(appSettings);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(
        "/scalar",
        options =>
        {
            options.WithTitle("Zeeq API Reference");
        }
    );
}

app.UseForwardedHeaders();
app.UseRequestDecompression();
app.UseAuthentication();
app.UseUserTokenValidation();
app.UseMembershipEnrichment();
app.UseAuthorization();

app.MapStaticSpas(app.Environment);
app.MapWebRootRedirect(app.Environment);
app.MapSpaFallbacks(app.Environment);

app.MapZeeqHealthEndpoints();
app.MapZeeqIdentityRoutes();
app.MapZeeqGitHubWebhooks(appSettings.GitHub);
app.MapZeeqEndpoints(appSettings);
app.MapMcp("/mcp").RequireAuthorization();

app.Run();

// This method checks if the application is running in a special mode for
// generating OpenAPI schemas and exits after running.
void HandleSchemaGenerationAndExit()
{
    if (Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider")
    {
        return;
    }

    Console.WriteLine("🖨️  Starting in OpenAPI generation mode...");

    var minimalBuilder = WebApplication.CreateBuilder(args);

    minimalBuilder
        .Services.AddOpenApi()
        .AddZeeqJsonConfig()
        .AddHttpContextAccessor()
        .AddZeeqEndpoints();

    var minimalApp = minimalBuilder.Build();

    minimalApp.MapZeeqEndpoints(new AppSettings { });
    minimalApp.MapOpenApi();

    minimalApp.Run();

    Environment.Exit(0); // ! EXIT: Generated OpenAPI schema
}
