using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest;

/// <summary>Manually queues a full resync for a Notion-backed library.</summary>
public sealed class TriggerNotionFullResyncHandler(
    ILibraryDocumentStore libraries,
    IZeeqMessagePublisher publisher,
    IngestSettings ingestSettings
) : IEndpointHandler
{
    /// <summary>Handles the manual Notion full-resync trigger request.</summary>
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

        var result = await IngestTriggerCoordinator.TryQueueNotionSyncAsync(
            libraries,
            publisher,
            ingestSettings,
            library,
            ExternalSyncScope.Full,
            IngestTriggerReason.Manual,
            ct
        );

        return result switch
        {
            IngestTriggerResult.NotSourceBacked => TypedResults.BadRequest(
                new IngestError("This library is not backed by Notion.")
            ),
            IngestTriggerResult.AlreadyInFlight => TypedResults.Conflict(
                new IngestError(
                    "A sync is already queued or running for this library. No new run token is "
                        + "issued for a redundant trigger — this is not a failure, retry once the "
                        + "in-progress sync completes if you need a fresh run."
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
            IngestTriggerResult.Quarantined => TypedResults.BadRequest(
                new IngestError("Notion libraries cannot be quarantined.")
            ),
            _ => throw new InvalidOperationException($"Unexpected trigger result: {result}"),
        };
    }
}
