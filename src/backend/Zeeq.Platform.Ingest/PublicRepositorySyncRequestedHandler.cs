using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Integrations.GitHub;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Consumes <see cref="PublicRepositorySyncRequested"/> and drives a public
/// source's repository sync to completion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Filter = union of subscribing libraries' effective filters</b> (spec
/// §2.4), not the source's own defaults — one shared table serves every
/// subscribing org, so narrowing one org's filter must not sweep another
/// org's in-scope files. Per-library override rule matches the private path
/// (non-empty library filter replaces the source default, no merge). Zero
/// subscribers falls back to the source's own default filters rather than an
/// empty union — an empty union would mean "include everything" per
/// <see cref="IngestFileFilter"/>'s semantics, which is worse than the
/// pre-union-logic behavior for an orphaned source; see
/// <see cref="UnionEffectiveFilter"/>'s remarks for the full rationale.
/// </para>
/// <para>
/// <b>Visibility re-verified every sync</b> (spec §13), not trusted from the
/// last <see cref="DocsPublicSource.Status"/>: <see cref="IPublicRepositoryVisibilityChecker"/>
/// makes a live anonymous check before each dispatch. Gone-private and
/// deleted are indistinguishable anonymously (both 404) — quarantined either
/// way. This check, not the defense-in-depth skip below, is what actually
/// enforces "never serve private content through the shared table." Transient
/// check failures (rate limit, network) reset to idle and retry — no
/// quarantine. Neither outcome writes a run record (nothing was dispatched).
/// No admin-alert channel exists yet in this codebase; <c>LogWarning</c> is
/// the interim signal (Datadog log-alert, same as the planned <c>auth_failure</c> alert).
/// </para>
/// <para>
/// <b>Dispatch failures don't rethrow.</b> A <c>DocsIngestRun</c> row already
/// exists under the message's <c>RunId</c> by the time <see cref="IRepositoryIngestDispatcher.RunAsync"/>
/// returns or throws; a Brighter redelivery would collide on that row's
/// primary key. Failures are logged and absorbed — <see cref="DocsIngestRun.Status"/>
/// is the record of outcome, not the handler's return/throw.
/// </para>
/// <para>
/// <b>Known gap:</b> if the post-run <c>sync_status</c> reset itself fails, the
/// source stays stuck at <c>running</c> until fixed manually — no atomic
/// claim/release yet; not worth building without real traffic to justify it.
/// </para>
/// </remarks>
[ConfigureConsumer<PublicRepositorySyncRequested>(
    "ingest.public.sync.handler",
    noOfPerformers: 2, // matches IngestSettings.MaxConcurrentPublic's default (2); see the type's remarks re: config vs. compile-time attribute constants
    bufferSize: 4,
    visibleTimeoutSeconds: 900 // clone + parse can take minutes; must exceed the longest expected run so Brighter doesn't redeliver mid-run
)]
public sealed class PublicRepositorySyncRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    IDocsPublicSourceStore sources,
    ILibraryDocumentStore libraries,
    IPublicRepositoryVisibilityChecker visibilityChecker,
    IRepositoryIngestDispatcher dispatcher,
    IngestSettings ingestSettings,
    ILogger<PublicRepositorySyncRequestedHandler> logger
) : ZeeqMessageHandler<PublicRepositorySyncRequested>(deadLetterWriter)
{
    /// <inheritdoc />
    protected override async Task<PublicRepositorySyncRequested> HandleMessageAsync(
        PublicRepositorySyncRequested message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);

        var source = await sources.GetByIdAsync(message.PublicSourceId, cancellationToken);
        if (source is null)
        {
            logger.LogWarning(
                "Ignoring sync for public source {PublicSourceId}: it no longer exists.",
                message.PublicSourceId
            );
            return message;
        }

        if (source.Status == "quarantined")
        {
            // Defense in depth for a message enqueued before this quarantine
            // landed — the manual-trigger endpoint and the live check below
            // already prevent new ones.
            logger.LogWarning(
                "Skipping sync for quarantined public source {PublicSourceId}.",
                source.Id
            );
            source.SyncStatus = "idle";
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await sources.UpdateAsync(source, cancellationToken);
            return message;
        }

        var visibility = await visibilityChecker.CheckAsync(source.RepoUrl, cancellationToken);
        switch (visibility)
        {
            case RepositoryVisibilityCheckResult.NotPubliclyAccessible:
                logger.LogWarning(
                    "Public source {PublicSourceId} ({RepoUrl}) is no longer publicly accessible — quarantining.",
                    source.Id,
                    source.RepoUrl
                );
                source.Status = "quarantined";
                source.SyncStatus = "idle";
                source.UpdatedAt = DateTimeOffset.UtcNow;
                await sources.UpdateAsync(source, cancellationToken);
                return message;
            case RepositoryVisibilityCheckResult.TransientError:
                logger.LogWarning(
                    "Visibility check failed for public source {PublicSourceId} ({RepoUrl}); will retry next sync.",
                    source.Id,
                    source.RepoUrl
                );
                await ResetSyncStateAsync(source, cancellationToken);
                return message;
        }

        source.SyncStatus = "running";
        source.UpdatedAt = DateTimeOffset.UtcNow;
        await sources.UpdateAsync(source, cancellationToken);

        var job = await BuildJobAsync(source, message, cancellationToken);

        try
        {
            var outcome = await dispatcher.RunAsync(job, cancellationToken);
            if (outcome.Kind == DispatchOutcomeKind.Completed)
            {
                source.SyncedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                logger.LogWarning(
                    "Public source {PublicSourceId} sync finished with {Outcome}: {Reason}",
                    source.Id,
                    outcome.Kind,
                    outcome.FailureReason
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unhandled exception dispatching public source {PublicSourceId} sync.",
                source.Id
            );
        }
        finally
        {
            await ResetSyncStateAsync(source, cancellationToken);
        }

        return message;
    }

    private async Task ResetSyncStateAsync(DocsPublicSource source, CancellationToken ct)
    {
        try
        {
            source.SyncStatus = "idle";
            source.NextSyncAt = NextSyncAt();
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await sources.UpdateAsync(source, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to reset sync_status for public source {PublicSourceId} after ingest; it "
                    + "will remain 'running' until manually corrected.",
                source.Id
            );
        }
    }

    private DateTimeOffset NextSyncAt()
    {
        var jitterSeconds = ingestSettings.SyncIntervalSeconds * ingestSettings.SyncJitterFraction;
        var jitter = (Random.Shared.NextDouble() * 2 * jitterSeconds) - jitterSeconds;
        return DateTimeOffset.UtcNow.AddSeconds(ingestSettings.SyncIntervalSeconds + jitter);
    }

    private async Task<RepositoryIngestJob> BuildJobAsync(
        DocsPublicSource source,
        PublicRepositorySyncRequested message,
        CancellationToken cancellationToken
    )
    {
        // NOTE: fetches full Library rows though only IncludeFilters/ExcludeFilters
        // are read below. Subscriber counts per public source are bounded by org
        // adoption (not expected to be large), and this only runs once per
        // scheduled sync, not per-request — a narrower projection wasn't judged
        // worth the added ILibraryDocumentStore surface for a Minor optimization.
        var subscribers = await libraries.ListLibrariesByPublicSourceIdAsync(
            source.Id,
            cancellationToken
        );

        return new RepositoryIngestJob
        {
            RunId = message.RunId,
            RunCreatedAtUtc = message.RunCreatedAtUtc,
            Kind = RepositorySourceKind.Public,
            RepoUrl = message.RepoUrl,
            PublicSourceId = source.Id,
            Trigger = message.Trigger,
            Filter = UnionEffectiveFilter(source, subscribers),
            TraceContext = message.TraceContext,
        };
    }

    /// <summary>
    /// Unions every subscriber's effective filter (spec §2.4). Per library:
    /// non-empty <c>IncludeFilters</c>/<c>ExcludeFilters</c> replace the
    /// source default entirely, never merge with it — same rule as the
    /// private path.
    /// </summary>
    /// <remarks>
    /// Zero subscribers is a reachable state, not just a theoretical edge
    /// case — a source's last subscribing library can be deleted, and
    /// <c>ClaimDueForSyncAsync</c> has no subscriber-count check, so an
    /// orphaned source keeps syncing on schedule indefinitely. An empty union
    /// there would mean "include everything" per <see cref="IngestFileFilter"/>'s
    /// semantics — ingesting the entire upstream repo for content nobody can
    /// query, which is strictly worse than this method's pre-union-filter
    /// behavior (which always used the source's own defaults). Falling back
    /// to the source defaults instead bounds that waste to what it was before
    /// this union logic existed; it does not eliminate it — no cascade
    /// quarantine/deactivation on zero subscribers exists, and building one is
    /// a separate concern from computing the correct filter.
    /// </remarks>
    private static EffectiveFilter UnionEffectiveFilter(
        DocsPublicSource source,
        IReadOnlyList<Library> subscribers
    )
    {
        if (subscribers.Count == 0)
        {
            return new EffectiveFilter(source.DefaultIncludeFilters, source.DefaultExcludeFilters);
        }

        var includes = new HashSet<string>(StringComparer.Ordinal);
        var excludes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var library in subscribers)
        {
            var effectiveIncludes =
                library.IncludeFilters.Length > 0
                    ? library.IncludeFilters
                    : source.DefaultIncludeFilters;
            var effectiveExcludes =
                library.ExcludeFilters.Length > 0
                    ? library.ExcludeFilters
                    : source.DefaultExcludeFilters;

            includes.UnionWith(effectiveIncludes);
            excludes.UnionWith(effectiveExcludes);
        }

        return new EffectiveFilter([.. includes], [.. excludes]);
    }

    private static Activity? StartActivity(PublicRepositorySyncRequested message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);

        return ZeeqTelemetry.Tracer.StartActivity(
            "ingest.public.sync_requested",
            ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("public_source.id", message.PublicSourceId),
                new("ingest.run.id", message.RunId),
                new("ingest.trigger", message.Trigger.ToString()),
            ]
        );
    }
}
