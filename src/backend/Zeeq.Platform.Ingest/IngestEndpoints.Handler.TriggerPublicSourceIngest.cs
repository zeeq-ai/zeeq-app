using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Manually triggers an ingest run for a public repository source.
/// </summary>
/// <remarks>
/// NOTE: see the equivalent remark on <see cref="TriggerLibraryIngestHandler"/>
/// — the idempotency and rate-limit checks (in <see cref="IngestTriggerCoordinator"/>,
/// shared with that handler) are the same read-then-write, non-atomic shape,
/// and the same reasoning applies for deferring an atomic store-level claim
/// until there's real traffic to justify it.
/// </remarks>
public sealed class TriggerPublicSourceIngestHandler(
    IDocsPublicSourceStore sources,
    IZeeqMessagePublisher publisher,
    IngestSettings ingestSettings
) : IEndpointHandler
{
    /// <summary>Handles the manual public-source ingest trigger request.</summary>
    public async Task<
        Results<
            Ok<TriggerIngestRunResponse>,
            NotFound,
            Conflict<IngestError>,
            JsonHttpResult<IngestError>
        >
    > HandleAsync(string publicSourceId, CancellationToken ct)
    {
        var source = await sources.GetByIdAsync(publicSourceId, ct);
        if (source is null)
        {
            return TypedResults.NotFound();
        }

        var result = await IngestTriggerCoordinator.TryQueuePublicSyncAsync(
            sources,
            publisher,
            ingestSettings,
            source,
            IngestTriggerReason.Manual,
            ct
        );

        return result switch
        {
            IngestTriggerResult.AlreadyInFlight => TypedResults.Conflict(
                new IngestError(
                    "A sync is already queued or running for this source. No new run token is "
                        + "issued for a redundant trigger — this is not a failure, retry once the "
                        + "in-progress sync completes if you need a fresh run."
                )
            ),
            IngestTriggerResult.Quarantined => TypedResults.Conflict(
                new IngestError(
                    "This source is quarantined (its upstream repository is no longer public) and cannot be synced."
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
