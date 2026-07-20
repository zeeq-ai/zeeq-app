using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Shared "queue a sync now" logic for a private-source library or a public
/// source, used by both the manual-trigger endpoints
/// (<see cref="TriggerLibraryIngestHandler"/>/<see cref="TriggerPublicSourceIngestHandler"/>)
/// and library creation (<c>Zeeq.Platform.Documents.CreateLibraryHandler</c>),
/// so the idempotency/rate-limit/publish behavior is defined exactly once.
/// </summary>
/// <remarks>
/// NOTE: idempotency and rate-limit checks are read-then-write, not atomic —
/// see the equivalent remark previously on <see cref="TriggerLibraryIngestHandler"/>.
/// Unchanged by this extraction; same reasoning applies (low-frequency,
/// human-triggered action, no production traffic yet to justify an atomic
/// store-level claim).
/// </remarks>
public static class IngestTriggerCoordinator
{
    /// <summary>Attempts to queue a sync for a private-source library.</summary>
    public static async Task<IngestTriggerResult> TryQueuePrivateSyncAsync(
        ILibraryDocumentStore libraries,
        IZeeqMessagePublisher publisher,
        IngestSettings ingestSettings,
        Library library,
        IngestTriggerReason trigger,
        CancellationToken ct
    )
    {
        if (library.SourceKind is null || library.SourceRepoUrl is null)
        {
            return new IngestTriggerResult.NotSourceBacked();
        }

        if (library.SyncStatus is "queued" or "running")
        {
            return new IngestTriggerResult.AlreadyInFlight();
        }

        // NOTE: This timestamp is persisted on the library lease and sent on
        // the queue message. Match PostgreSQL timestamptz precision up front so
        // stale-message guards can compare the reloaded row exactly.
        var now = PostgresTimestampPrecision.TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var window = TimeSpan.FromSeconds(ingestSettings.ManualTriggerWindowSeconds);
        var recentTriggers = library
            .ManualTriggerHistory.Where(triggeredAt => now - triggeredAt <= window)
            .ToArray();

        if (recentTriggers.Length >= ingestSettings.ManualTriggerMaxInWindow)
        {
            return new IngestTriggerResult.RateLimited(recentTriggers.Min() + window);
        }

        var runId = $"run_{Guid.CreateVersion7():N}";

        await libraries.UpdateSyncLeaseAsync(
            library.OrganizationId,
            library.Id,
            syncStatus: "queued",
            nextSyncAt: library.NextSyncAt,
            manualTriggerHistory: [.. recentTriggers, now],
            sourceSyncedAt: library.SourceSyncedAt,
            activeSyncRunId: runId,
            activeSyncRunCreatedAtUtc: now,
            syncQueuedAtUtc: now,
            syncStartedAtUtc: null,
            ct
        );

        await publisher.PublishAsync(
            new PrivateRepositorySyncRequested
            {
                OrganizationId = library.OrganizationId,
                TeamId = library.TeamId,
                RunId = runId,
                RunCreatedAtUtc = now,
                LibraryId = library.Id,
                RepoUrl = library.SourceRepoUrl,
                Trigger = trigger,
                TraceContext = ZeeqTelemetry.CaptureCurrentTraceContext(),
            },
            ct
        );

        return new IngestTriggerResult.Queued(
            runId,
            now,
            IngestRunViewToken.Encode(now, RepositorySourceKind.Private)
        );
    }

    /// <summary>Attempts to queue a sync for a public source.</summary>
    public static async Task<IngestTriggerResult> TryQueuePublicSyncAsync(
        IDocsPublicSourceStore sources,
        IZeeqMessagePublisher publisher,
        IngestSettings ingestSettings,
        DocsPublicSource source,
        IngestTriggerReason trigger,
        CancellationToken ct
    )
    {
        if (source.SyncStatus is "queued" or "running")
        {
            return new IngestTriggerResult.AlreadyInFlight();
        }

        if (source.Status == "quarantined")
        {
            return new IngestTriggerResult.Quarantined();
        }

        // NOTE: This timestamp is persisted on the public-source lease and sent
        // on the queue message. Match PostgreSQL timestamptz precision up front
        // so stale-message guards can compare the reloaded row exactly.
        var now = PostgresTimestampPrecision.TruncateToMicroseconds(DateTimeOffset.UtcNow);
        var window = TimeSpan.FromSeconds(ingestSettings.ManualTriggerWindowSeconds);
        var recentTriggers = source
            .ManualTriggerHistory.Where(triggeredAt => now - triggeredAt <= window)
            .ToArray();

        if (recentTriggers.Length >= ingestSettings.ManualTriggerMaxInWindow)
        {
            return new IngestTriggerResult.RateLimited(recentTriggers.Min() + window);
        }

        var runId = $"run_{Guid.CreateVersion7():N}";

        source.SyncStatus = "queued";
        source.ActiveSyncRunId = runId;
        source.ActiveSyncRunCreatedAtUtc = now;
        source.SyncQueuedAtUtc = now;
        source.SyncStartedAtUtc = null;
        source.ManualTriggerHistory = [.. recentTriggers, now];
        source.UpdatedAt = now;
        await sources.UpdateAsync(source, ct);

        await publisher.PublishAsync(
            new PublicRepositorySyncRequested
            {
                RunId = runId,
                RunCreatedAtUtc = now,
                PublicSourceId = source.Id,
                RepoUrl = source.RepoUrl,
                Trigger = trigger,
                TraceContext = ZeeqTelemetry.CaptureCurrentTraceContext(),
            },
            ct
        );

        return new IngestTriggerResult.Queued(
            runId,
            now,
            IngestRunViewToken.Encode(now, RepositorySourceKind.Public)
        );
    }
}

/// <summary>Outcome of an <see cref="IngestTriggerCoordinator"/> queue attempt.</summary>
public abstract record IngestTriggerResult
{
    /// <summary>A new run was queued and published.</summary>
    public sealed record Queued(string RunId, DateTimeOffset RunCreatedAtUtc, string ViewToken)
        : IngestTriggerResult;

    /// <summary>A sync is already queued or running; no new run was created.</summary>
    public sealed record AlreadyInFlight : IngestTriggerResult;

    /// <summary>The manual-trigger rate limit is exhausted until the given time.</summary>
    public sealed record RateLimited(DateTimeOffset RetryAt) : IngestTriggerResult;

    /// <summary>The public source is quarantined and cannot be synced.</summary>
    public sealed record Quarantined : IngestTriggerResult;

    /// <summary>The library has no linked repository source.</summary>
    public sealed record NotSourceBacked : IngestTriggerResult;
}
