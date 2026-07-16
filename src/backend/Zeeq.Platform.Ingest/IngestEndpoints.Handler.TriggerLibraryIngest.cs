using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Manually triggers an ingest run for a library's mapped repository — private
/// source or, since the library-GitHub-import UI slice, a public source this
/// library subscribes to.
/// </summary>
/// <remarks>
/// Deliberately org-scoped (not admin-scoped like
/// <see cref="TriggerPublicSourceIngestHandler"/>) even for the public-source
/// branch: any org user who owns a library subscribing to a public source can
/// trigger that source's sync through their own library, without needing
/// system-admin rights. There is no cross-project dependency from
/// <c>Zeeq.Platform.Documents</c> (library creation) back into this project
/// to publish the initial "queue immediately" sync directly — that would
/// create a circular reference (Ingest → Integrations.GitHub → CodeReviews →
/// Mcp.Documents → Platform.Documents). Instead, the client calls this same
/// endpoint as an immediate follow-up request right after creating a
/// repo-sourced library, reusing this one code path (and its rate limit) for
/// both "queue immediately on create" and "Sync now" from the status panel.
/// <para>
/// Rate limiting and idempotency read/write <see cref="Library.SyncStatus"/> /
/// <see cref="Library.ManualTriggerHistory"/> (private) or
/// <see cref="DocsPublicSource.SyncStatus"/> / <c>ManualTriggerHistory</c>
/// (public) via <see cref="IngestTriggerCoordinator"/>, shared with
/// <see cref="TriggerPublicSourceIngestHandler"/>.
/// </para>
/// <para>
/// NOTE: the idempotency and rate-limit checks are read-then-write, not
/// atomic — two concurrent requests can both observe the pre-update state,
/// both pass, and both enqueue a sync (idempotency) or both write a trimmed
/// history that drops one caller's timestamp (rate limit undercounted, not a
/// data-integrity issue since no duplicate work results from that half).
/// Known and accepted for this phase: this endpoint is a low-frequency,
/// human-triggered action, not a hot path, and there's no production traffic
/// yet to justify the store-level atomic-claim work (an
/// `UPDATE ... WHERE sync_status NOT IN (...) RETURNING *` guard, mirroring
/// `IDocsPublicSourceStore.ClaimDueForSyncAsync`'s `SKIP LOCKED` pattern)
/// until it's shown to matter in practice.
/// </para>
/// </remarks>
public sealed class TriggerLibraryIngestHandler(
    ILibraryDocumentStore libraries,
    IDocsPublicSourceStore publicSources,
    IZeeqMessagePublisher publisher,
    IngestSettings ingestSettings
) : IEndpointHandler
{
    /// <summary>Handles the manual library ingest trigger request.</summary>
    public async Task<
        Results<
            Ok<TriggerIngestRunResponse>,
            BadRequest<IngestError>,
            NotFound,
            Conflict<IngestError>,
            JsonHttpResult<IngestError>
        >
    > HandleAsync(string orgId, string name, ClaimsPrincipal user, CancellationToken ct)
    {
        var library = await libraries.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        IngestTriggerResult result;
        if (library.PublicSourceId is { } publicSourceId)
        {
            var source = await publicSources.GetByIdAsync(publicSourceId, ct);
            if (source is null)
            {
                return TypedResults.NotFound();
            }

            result = await IngestTriggerCoordinator.TryQueuePublicSyncAsync(
                publicSources,
                publisher,
                ingestSettings,
                source,
                IngestTriggerReason.Manual,
                ct
            );
        }
        else
        {
            result = await IngestTriggerCoordinator.TryQueuePrivateSyncAsync(
                libraries,
                publisher,
                ingestSettings,
                library,
                IngestTriggerReason.Manual,
                ct
            );
        }

        return result switch
        {
            IngestTriggerResult.NotSourceBacked => TypedResults.BadRequest(
                new IngestError(
                    "This library has no linked repository to sync. Import a repository source first."
                )
            ),
            IngestTriggerResult.AlreadyInFlight => TypedResults.Conflict(
                new IngestError(
                    "A sync is already queued or running for this library. No new run token is "
                        + "issued for a redundant trigger — this is not a failure, retry once the "
                        + "in-progress sync completes if you need a fresh run."
                )
            ),
            IngestTriggerResult.Quarantined => TypedResults.Conflict(
                new IngestError(
                    "The public source backing this library is quarantined (its upstream "
                        + "repository is no longer public) and cannot be synced."
                )
            ),
            IngestTriggerResult.RateLimited rateLimited => TypedResults.Json(
                new IngestError(
                    $"Rate limited; next trigger available at {rateLimited.RetryAt:O}."
                ),
                statusCode: StatusCodes.Status429TooManyRequests
            ),
            IngestTriggerResult.Queued queued => TypedResults.Ok(
                new TriggerIngestRunResponse(queued.RunId, queued.RunCreatedAtUtc, queued.ViewToken)
            ),
            _ => throw new InvalidOperationException($"Unexpected trigger result: {result}"),
        };
    }
}
