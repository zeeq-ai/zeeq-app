using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Periodically clears queued/running repository sync leases that outlived their timeout.
/// </summary>
public sealed class IngestStalledSyncCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    IngestSettings ingestSettings,
    ILogger<IngestStalledSyncCleanupHostedService> logger
) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ingestSettings.StalledSyncSweepPeriod);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Stalled ingest sync cleanup failed; will retry next tick.");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Runs one cleanup pass. Internal so tests can drive it without waiting on the timer.
    /// </summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "ingest.stalled_sync_cleanup.sweep",
            ActivityKind.Internal
        );

        var now = DateTimeOffset.UtcNow;
        await using var scope = scopeFactory.CreateAsyncScope();
        var libraries = scope.ServiceProvider.GetRequiredService<ILibraryDocumentStore>();
        var publicSources = scope.ServiceProvider.GetRequiredService<IDocsPublicSourceStore>();

        var privateResets = await libraries.ResetStalledSyncsAsync(
            now,
            ingestSettings.QueuedSyncStaleAfter,
            ingestSettings.RunningSyncStaleAfter,
            ingestSettings.SchedulerBatchSize,
            cancellationToken
        );
        var publicResets = await publicSources.ResetStalledSyncsAsync(
            now,
            ingestSettings.QueuedSyncStaleAfter,
            ingestSettings.RunningSyncStaleAfter,
            ingestSettings.SchedulerBatchSize,
            cancellationToken
        );

        // NOTE: Each store marks the active run Stalled before clearing its
        // source lease in the same transaction. The sweep only counts committed
        // recoveries here so partial failures remain retryable.
        var stalledRunCount = privateResets.Count + publicResets.Count;

        activity?.SetTag("ingest.stalled_sync_cleanup.private_reset_count", privateResets.Count);
        activity?.SetTag("ingest.stalled_sync_cleanup.public_reset_count", publicResets.Count);
        activity?.SetTag("ingest.stalled_sync_cleanup.run_stalled_count", stalledRunCount);

        if (privateResets.Count > 0 || publicResets.Count > 0 || stalledRunCount > 0)
        {
            logger.LogWarning(
                "Cleared stalled ingest syncs. PrivateResetCount={PrivateResetCount}, PublicResetCount={PublicResetCount}, RunStalledCount={RunStalledCount}.",
                privateResets.Count,
                publicResets.Count,
                stalledRunCount
            );
        }
    }
}
