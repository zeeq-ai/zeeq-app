using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Consumes <see cref="PrivateRepositorySyncRequested"/> and drives a private
/// library's repository sync to completion.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="PublicRepositorySyncRequestedHandler"/> closely — same
/// no-rethrow-on-dispatch-failure reasoning (a <c>DocsIngestRun</c> row
/// already exists under the message's <c>RunId</c> by the time
/// <see cref="IRepositoryIngestDispatcher.RunAsync"/> returns or throws, so a
/// Brighter retry would collide on the run store's primary key), and the same
/// known gap if the final <c>sync_status='idle'</c> reset itself fails.
/// </para>
/// <para>
/// <b>Effective filter</b> uses the same override rule established in Phase
/// 0's <c>LibraryBuilder</c> tests: <see cref="Library.IncludeFilters"/>
/// replaces <see cref="Library.SourceDefaultIncludeFilters"/> entirely when
/// non-empty, rather than merging with it (same for exclude filters).
/// </para>
/// <para>
/// <b>Installation id is looked up, not required.</b> A missing installation
/// isn't necessarily fatal — <c>IngestGitHubTokenProvider</c>'s resolve chain
/// still falls back to <c>GH_TOKEN</c> when <c>AlwaysUseGhTokenForSync</c> is
/// set. This handler passes through whatever
/// <see cref="IGitHubInstallationStore.FindActiveForOrganizationAsync"/>
/// finds (including <see langword="null"/>) rather than duplicating the token
/// provider's own availability logic; a genuinely unresolvable token still
/// surfaces as an <c>InvalidOperationException</c> the dispatcher already
/// catches and records as a Failed/auth-failure run.
/// </para>
/// </remarks>
[ConfigureConsumer<PrivateRepositorySyncRequested>(
    "ingest.private.sync.handler",
    // PrivateRepositorySyncRequested is ISystemMessage (single channel), not
    // ITenantMessage — so unlike a tenant-fanned handler, this noOfPerformers
    // value IS the real, exact concurrency cap, matching
    // IngestSettings.MaxConcurrentPrivate's default (4) precisely. Before this
    // message was converted from ITenantMessage, the same value here was
    // silently multiplied by the tenant bucket count (20 by default:
    // 8 priority + 8 default + 4 low), giving an actual cap of 80 concurrent
    // private syncs — 20x the documented/intended limit — while also being
    // 20x more Brighter performer threads than needed, since priority lanes
    // aren't a requirement for this feature. See the compile-time-constant
    // caveat below re: config vs. this attribute value.
    noOfPerformers: 4, // matches IngestSettings.MaxConcurrentPrivate's default (4); see PublicRepositorySyncRequestedHandler's remarks re: config vs. compile-time attribute constants
    bufferSize: 4,
    visibleTimeoutSeconds: 900 // clone + parse can take minutes; must exceed the longest expected run so Brighter doesn't redeliver mid-run
)]
public sealed class PrivateRepositorySyncRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    ILibraryDocumentStore libraries,
    IGitHubInstallationStore installations,
    IRepositoryIngestDispatcher dispatcher,
    IngestSettings ingestSettings,
    ILogger<PrivateRepositorySyncRequestedHandler> logger
) : ZeeqMessageHandler<PrivateRepositorySyncRequested>(deadLetterWriter)
{
    /// <inheritdoc />
    protected override async Task<PrivateRepositorySyncRequested> HandleMessageAsync(
        PrivateRepositorySyncRequested message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);

        var library = await libraries.GetLibraryByIdAsync(
            message.OrganizationId,
            message.LibraryId,
            cancellationToken
        );
        if (library is null)
        {
            logger.LogWarning(
                "Ignoring sync for library {LibraryId} in org {OrganizationId}: it no longer exists.",
                message.LibraryId,
                message.OrganizationId
            );
            return message;
        }

        await SetSyncStatusAsync(library, "running", cancellationToken);

        var installation = await installations.FindActiveForOrganizationAsync(
            message.OrganizationId,
            cancellationToken
        );
        var job = BuildJob(library, message, installation?.InstallationId);

        try
        {
            var outcome = await dispatcher.RunAsync(job, cancellationToken);
            if (outcome.Kind == DispatchOutcomeKind.Completed)
            {
                library.SourceSyncedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                logger.LogWarning(
                    "Library {LibraryId} sync finished with {Outcome}: {Reason}",
                    library.Id,
                    outcome.Kind,
                    outcome.FailureReason
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unhandled exception dispatching library {LibraryId} sync.",
                library.Id
            );
        }
        finally
        {
            await ResetSyncStateAsync(library, cancellationToken);
        }

        return message;
    }

    private async Task SetSyncStatusAsync(Library library, string syncStatus, CancellationToken ct)
    {
        await libraries.UpdateSyncStateAsync(
            library.OrganizationId,
            library.Id,
            syncStatus,
            library.NextSyncAt,
            library.ManualTriggerHistory,
            library.SourceSyncedAt,
            ct
        );
        library.SyncStatus = syncStatus;
    }

    private async Task ResetSyncStateAsync(Library library, CancellationToken ct)
    {
        try
        {
            var nextSyncAt = NextSyncAt();
            await libraries.UpdateSyncStateAsync(
                library.OrganizationId,
                library.Id,
                "idle",
                nextSyncAt,
                library.ManualTriggerHistory,
                library.SourceSyncedAt,
                ct
            );
            library.SyncStatus = "idle";
            library.NextSyncAt = nextSyncAt;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to reset sync_status for library {LibraryId} after ingest; it will remain "
                    + "'running' until manually corrected.",
                library.Id
            );
        }
    }

    private DateTimeOffset NextSyncAt()
    {
        var jitterSeconds = ingestSettings.SyncIntervalSeconds * ingestSettings.SyncJitterFraction;
        var jitter = (Random.Shared.NextDouble() * 2 * jitterSeconds) - jitterSeconds;
        return DateTimeOffset.UtcNow.AddSeconds(ingestSettings.SyncIntervalSeconds + jitter);
    }

    private static RepositoryIngestJob BuildJob(
        Library library,
        PrivateRepositorySyncRequested message,
        long? installationId
    ) =>
        new()
        {
            RunId = message.RunId,
            RunCreatedAtUtc = message.RunCreatedAtUtc,
            Kind = RepositorySourceKind.Private,
            RepoUrl = message.RepoUrl,
            OrganizationId = library.OrganizationId,
            LibraryId = library.Id,
            TeamId = library.TeamId,
            InstallationId = installationId,
            Trigger = message.Trigger,
            Filter = new EffectiveFilter(
                library.IncludeFilters.Length > 0
                    ? library.IncludeFilters
                    : library.SourceDefaultIncludeFilters,
                library.ExcludeFilters.Length > 0
                    ? library.ExcludeFilters
                    : library.SourceDefaultExcludeFilters
            ),
            TraceContext = message.TraceContext,
        };

    private static Activity? StartActivity(PrivateRepositorySyncRequested message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);

        return ZeeqTelemetry.Tracer.StartActivity(
            "ingest.private.sync_requested",
            ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("organization.id", message.OrganizationId),
                new("library.id", message.LibraryId),
                new("ingest.run.id", message.RunId),
                new("ingest.trigger", message.Trigger.ToString()),
            ]
        );
    }
}
