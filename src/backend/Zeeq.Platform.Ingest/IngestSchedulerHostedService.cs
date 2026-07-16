using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Periodically claims due public repository sources and publishes a sync
/// request for each one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both source kinds.</b> Public sources are claimed via
/// <see cref="IDocsPublicSourceStore.ClaimDueForSyncAsync"/> and publish
/// <see cref="PublicRepositorySyncRequested"/>; private-source libraries via
/// <see cref="ILibraryDocumentStore.ClaimDueForSyncAsync"/> and
/// <see cref="PrivateRepositorySyncRequested"/> — same tick, two independent
/// claim/publish passes.
/// </para>
/// <para>
/// <b>Claiming is already atomic.</b> Both claim methods use
/// <c>UPDATE ... FOR UPDATE SKIP LOCKED</c> and transition claimed rows to
/// <c>queued</c> themselves — this service does not need its own locking, and
/// multiple scheduler instances ticking concurrently (e.g. more than one
/// worker replica) race safely rather than double-dispatching the same
/// source.
/// </para>
/// <para>
/// <b>Registered worker-only, not web.</b> Unlike the DI registrations added
/// for <c>Zeeq.Platform.Ingest</c>/<c>Zeeq.Platform.Dispatch.Process</c>
/// (added to both <c>Program.cs</c> and <c>ZeeqWorkerHost</c> purely so
/// Scrutor/Brighter catalog scanning sees the types), this is a live
/// <see cref="BackgroundService"/> that starts polling the moment it's
/// registered. Running it on every web replica would mean every replica
/// polling on its own timer — wasteful, even though the atomic claim makes it
/// safe. It is registered only by <c>ZeeqWorkerHost</c>, matching
/// <c>ZeeqWorkerHeartbeatService</c>'s precedent for worker-only background
/// work. This means it does not run in this codebase's local Aspire topology
/// today (a single web-mode process, no separate worker resource) — verified
/// by unit test, not a live smoke test; note this if a live run is needed.
/// </para>
/// </remarks>
public sealed class IngestSchedulerHostedService(
    IServiceScopeFactory scopeFactory,
    IngestSettings ingestSettings,
    ILogger<IngestSchedulerHostedService> logger
) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(ingestSettings.SchedulerPeriodSeconds)
        );

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ingest scheduler tick failed; will retry next tick.");
            }
        }
    }

    /// <summary>
    /// Runs one scheduler pass: claim due sources, publish a sync request for
    /// each. Internal (not private) so tests can drive it directly without
    /// waiting on the <see cref="PeriodicTimer"/>.
    /// </summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "ingest.scheduler.tick",
            ActivityKind.Internal
        );

        await using var scope = scopeFactory.CreateAsyncScope();
        var sources = scope.ServiceProvider.GetRequiredService<IDocsPublicSourceStore>();
        var libraries = scope.ServiceProvider.GetRequiredService<ILibraryDocumentStore>();
        var publisher = scope.ServiceProvider.GetRequiredService<IZeeqMessagePublisher>();

        var traceContext = ZeeqTelemetry.CaptureCurrentTraceContext();

        var claimedSources = await sources.ClaimDueForSyncAsync(
            ingestSettings.SchedulerBatchSize,
            cancellationToken
        );
        activity?.SetTag("ingest.scheduler.claimed_public_count", claimedSources.Count);

        foreach (var source in claimedSources)
        {
            var runId = $"run_{Guid.CreateVersion7():N}";
            var now = DateTimeOffset.UtcNow;

            await publisher.PublishAsync(
                new PublicRepositorySyncRequested
                {
                    RunId = runId,
                    RunCreatedAtUtc = now,
                    PublicSourceId = source.Id,
                    RepoUrl = source.RepoUrl,
                    Trigger = IngestTriggerReason.Scheduled,
                    TraceContext = traceContext,
                },
                cancellationToken
            );

            logger.LogInformation(
                "Scheduled sync for public source {PublicSourceId} ({RepoUrl}), run {RunId}.",
                source.Id,
                source.RepoUrl,
                runId
            );
        }

        var claimedLibraries = await libraries.ClaimDueForSyncAsync(
            ingestSettings.SchedulerBatchSize,
            cancellationToken
        );
        activity?.SetTag("ingest.scheduler.claimed_private_count", claimedLibraries.Count);

        foreach (var library in claimedLibraries)
        {
            var runId = $"run_{Guid.CreateVersion7():N}";
            var now = DateTimeOffset.UtcNow;

            await publisher.PublishAsync(
                new PrivateRepositorySyncRequested
                {
                    RunId = runId,
                    RunCreatedAtUtc = now,
                    OrganizationId = library.OrganizationId,
                    TeamId = library.TeamId,
                    LibraryId = library.Id,
                    RepoUrl = library.SourceRepoUrl!,
                    Trigger = IngestTriggerReason.Scheduled,
                    TraceContext = traceContext,
                },
                cancellationToken
            );

            logger.LogInformation(
                "Scheduled sync for library {LibraryId} in org {OrganizationId}, run {RunId}.",
                library.Id,
                library.OrganizationId,
                runId
            );
        }
    }
}
