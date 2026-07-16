using Zeeq.Core.Llm;
using Zeeq.Integrations.GitHub;
using Zeeq.Platform.CodeReviews;
using Zeeq.Platform.Dispatch.Process;
using Zeeq.Platform.Documents;
using Zeeq.Platform.Ingest;
using Zeeq.Platform.Llm;
using Zeeq.Platform.Metrics;
using Zeeq.Platform.Storage.Google;
using Microsoft.Extensions.Options;

namespace Zeeq.Runtime.Server;

/// <summary>
/// Starts the standalone message worker using the generic host.
/// </summary>
/// <remarks>
/// Worker mode intentionally does not create a <see cref="WebApplication" />.
/// Cloud Run worker pools have no HTTP ingress, so this path only registers
/// infrastructure required by message consumers.
/// </remarks>
internal static class ZeeqWorkerHost
{
    /// <summary>
    /// Runs the message worker until the host is shut down.
    /// </summary>
    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.AddZeeqLogging();

        Log.Information(
            "zeeq-runtime {Sha} built {BuildTimeUtc} UTC ({BuildTimeEst})",
            GitVersionInfo.Sha ?? "unknown",
            GitVersionInfo.BuildTimeUtc?.ToString("o"),
            GitVersionInfo.BuildTimeEst ?? "unknown"
        );

        try
        {
            var appSettings =
                builder.Configuration.GetSection(nameof(AppSettings)).Get<AppSettings>()
                ?? throw new InvalidOperationException("AppSettings configuration is required.");

            var workerAppSettings = appSettings with
            {
                Database = appSettings.Database with
                {
                    ConnectionString = appSettings.Database.EffectiveWorkerConnectionString,
                },
            };

            // Worker mode replaces the settings exposed by DI with the effective
            // worker settings so services reading AppSettings/IOptions<AppSettings>
            // see the same connection string used by data, cache, and messaging.
            builder.Services.AddSingleton(workerAppSettings);
            builder.Services.AddSingleton<IOptions<AppSettings>>(Options.Create(workerAppSettings));
            builder.Services.AddHostedService<ZeeqWorkerGlobalExceptionLogger>();
            builder.Services.AddHostedService<ZeeqWorkerHeartbeatService>();
            // Registered before AddZeeqMessaging below so this check's
            // StartAsync runs — and can fail fast — before any message
            // consumer hosted service starts accepting ingest jobs.
            builder.Services.AddHostedService<ZeeqIngestWorkspaceStartupCheck>();

            builder
                .Services.AddZeeqTelemetry()
                .AddZeeqData(workerAppSettings)
                .AddZeeqCache(workerAppSettings)
                .AddZeeqMessaging(
                    workerAppSettings,
                    builder.Configuration,
                    ZeeqRuntimeMode.MessagingRole
                )
                .AddZeeqGitHubIntegration(workerAppSettings)
                .AddZeeqLlm(workerAppSettings.Llm, builder.Environment)
                .AddZeeqLlmPlatform()
                .AddGoogleKmsDataEncryption(workerAppSettings.Llm)
                .AddZeeqCodeReviews(workerAppSettings.CodeReview)
                .AddZeeqIngest(workerAppSettings)
                .AddZeeqIngestScheduler()
                .AddZeeqDispatchProcess();

            LlmStartupDiagnostics.LogEmbeddingConfigurationStatus(workerAppSettings.Llm);

            // The snippet indexing sweep runs on the worker pool (its natural home): a live
            // BackgroundService that drains the ProcessingStatus backlog. Settings slice + hosted
            // service, mirroring AddZeeqIngest/AddZeeqIngestScheduler.
            builder.Services.AddZeeqDocuments(workerAppSettings);
            builder.Services.AddZeeqSnippetIndexing();

            // Metrics capture pipeline. The worker is a producer (review metrics) and,
            // as a consumer, also hosts the MetricBatchMessage handler that writes to
            // zeeq_metric_events.
            builder.Services.AddZeeqMetrics();

            var host = builder.Build();

            await host.Services.UseZeeqDataAsync(workerAppSettings);
            await host.Services.UseZeeqCacheAsync(workerAppSettings);

            await host.RunAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Fatal(ex, "Unhandled exception escaped the Zeeq worker host.");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
