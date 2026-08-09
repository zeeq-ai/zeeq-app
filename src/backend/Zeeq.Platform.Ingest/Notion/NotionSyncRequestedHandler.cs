using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest;

/// <summary>Consumes a queued Notion sync under the library's active-run lease.</summary>
[ConfigureConsumer<NotionSyncRequested>(
    "ingest.notion.sync.handler",
    noOfPerformers: NotionIngestConcurrency.PerformerCount,
    bufferSize: NotionIngestConcurrency.PerformerCount,
    visibleTimeoutSeconds: 900
)]
public sealed class NotionSyncRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    ILibraryDocumentStore libraries,
    INotionIngestDispatcher dispatcher,
    IngestSettings ingestSettings,
    ILogger<NotionSyncRequestedHandler> logger
) : ZeeqMessageHandler<NotionSyncRequested>(deadLetterWriter)
{
    /// <inheritdoc />
    protected override async Task<NotionSyncRequested> HandleMessageAsync(
        NotionSyncRequested message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);
        var library = await libraries.GetLibraryByIdAsync(
            message.OrganizationId,
            message.LibraryId,
            cancellationToken
        );
        if (
            library is null
            || library.SourceKind != RepositorySourceKind.Notion.ToString()
            || library.ExternalSource?.Notion is null
            || library.SyncStatus != "queued"
            || !IsCurrentSync(library, message)
        )
        {
            logger.LogWarning(
                "Ignoring stale or invalid Notion sync for library {LibraryId} in org {OrganizationId}, run {RunId}.",
                message.LibraryId,
                message.OrganizationId,
                message.RunId
            );
            return message;
        }

        if (!await SetRunningAsync(library, message, cancellationToken))
        {
            logger.LogWarning(
                "Ignoring Notion sync for library {LibraryId}: active run changed before start.",
                library.Id
            );
            return message;
        }

        NotionDispatchOutcome? outcome = null;
        try
        {
            outcome = await dispatcher.RunAsync(BuildJob(library, message), cancellationToken);
            if (outcome.Status is IngestRunStatus.Succeeded or IngestRunStatus.Partial)
            {
                library.SourceSyncedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                logger.LogWarning(
                    "Notion sync for library {LibraryId} failed: {Reason}",
                    library.Id,
                    outcome.FailureReason
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Unhandled exception dispatching Notion library {LibraryId}.",
                library.Id
            );
        }
        finally
        {
            await ResetSyncStateAsync(library, message, outcome, cancellationToken);
        }

        return message;
    }

    private async Task<bool> SetRunningAsync(
        Library library,
        NotionSyncRequested message,
        CancellationToken cancellationToken
    )
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var updated = await libraries.TryUpdateCurrentSyncLeaseAsync(
            library.OrganizationId,
            library.Id,
            message.RunId,
            message.RunCreatedAtUtc,
            expectedSyncStatus: "queued",
            "running",
            library.NextSyncAt,
            library.ManualTriggerHistory,
            library.SourceSyncedAt,
            message.RunId,
            message.RunCreatedAtUtc,
            library.SyncQueuedAtUtc,
            startedAtUtc,
            library.NextFullResyncAt,
            cancellationToken
        );
        if (updated)
        {
            library.SyncStatus = "running";
            library.SyncStartedAtUtc = startedAtUtc;
        }

        return updated;
    }

    private async Task ResetSyncStateAsync(
        Library library,
        NotionSyncRequested message,
        NotionDispatchOutcome? outcome,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var nextSyncAt = NextSyncAt(ingestSettings.SyncIntervalSeconds);
            var nextFullResyncAt =
                message.Scope is ExternalSyncScope.Full
                && outcome?.Status is IngestRunStatus.Succeeded
                    ? NextSyncAt(ingestSettings.NotionFullResyncIntervalSeconds)
                    : library.NextFullResyncAt;
            var updated = await libraries.TryUpdateCurrentSyncLeaseAsync(
                library.OrganizationId,
                library.Id,
                message.RunId,
                message.RunCreatedAtUtc,
                expectedSyncStatus: null,
                "idle",
                nextSyncAt,
                library.ManualTriggerHistory,
                library.SourceSyncedAt,
                activeSyncRunId: null,
                activeSyncRunCreatedAtUtc: null,
                syncQueuedAtUtc: null,
                syncStartedAtUtc: null,
                nextFullResyncAt,
                cancellationToken
            );
            if (!updated)
            {
                logger.LogWarning(
                    "Skipped final Notion sync-state reset for library {LibraryId}: active run changed.",
                    library.Id
                );
                return;
            }

            library.SyncStatus = "idle";
            library.NextSyncAt = nextSyncAt;
            library.NextFullResyncAt = nextFullResyncAt;
            library.ActiveSyncRunId = null;
            library.ActiveSyncRunCreatedAtUtc = null;
            library.SyncQueuedAtUtc = null;
            library.SyncStartedAtUtc = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Failed to reset sync state for Notion library {LibraryId}; stalled recovery will retry it.",
                library.Id
            );
        }
    }

    private DateTimeOffset NextSyncAt(int intervalSeconds)
    {
        var jitterSeconds = intervalSeconds * ingestSettings.SyncJitterFraction;
        var jitter = (Random.Shared.NextDouble() * 2 * jitterSeconds) - jitterSeconds;
        return DateTimeOffset.UtcNow.AddSeconds(intervalSeconds + jitter);
    }

    private static bool IsCurrentSync(Library library, NotionSyncRequested message) =>
        library.ActiveSyncRunId == message.RunId
        && library.ActiveSyncRunCreatedAtUtc == message.RunCreatedAtUtc;

    private static NotionIngestJob BuildJob(Library library, NotionSyncRequested message) =>
        new()
        {
            RunId = message.RunId,
            RunCreatedAtUtc = message.RunCreatedAtUtc,
            OrganizationId = library.OrganizationId,
            TeamId = library.TeamId,
            LibraryId = library.Id,
            Scope = message.Scope,
            Trigger = message.Trigger,
            Filter = new EffectiveFilter(
                library.IncludeFilters.Length > 0
                    ? library.IncludeFilters
                    : library.SourceDefaultIncludeFilters,
                library.ExcludeFilters.Length > 0
                    ? library.ExcludeFilters
                    : library.SourceDefaultExcludeFilters
            ),
            SourceReference = $"notion://library/{library.Id}",
            TraceContext = message.TraceContext,
        };

    private static Activity? StartActivity(NotionSyncRequested message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);
        return ZeeqTelemetry.Tracer.StartActivity(
            "ingest.notion.sync_requested",
            ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("organization.id", message.OrganizationId),
                new("library.id", message.LibraryId),
                new("ingest.run.id", message.RunId),
                new("ingest.scope", message.Scope.ToString()),
                new("ingest.trigger", message.Trigger.ToString()),
            ]
        );
    }
}
