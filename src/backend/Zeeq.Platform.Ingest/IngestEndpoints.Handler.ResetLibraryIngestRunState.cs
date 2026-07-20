using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Clears the queued/running sync lease for a private-source library.
/// </summary>
public sealed class ResetLibraryIngestRunStateHandler(ILibraryDocumentStore libraries)
    : IEndpointHandler
{
    /// <summary>Handles the manual library ingest reset request.</summary>
    public async Task<
        Results<Ok<ResetLibraryIngestRunStateResponse>, BadRequest<IngestError>, NotFound>
    > HandleAsync(string orgId, string name, ClaimsPrincipal user, CancellationToken ct)
    {
        var library = await libraries.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        if (library.SourceKind is null || library.SourceRepoUrl is null)
        {
            return TypedResults.BadRequest(
                new IngestError(
                    "Manual reset is only available for libraries backed by a private repository source."
                )
            );
        }

        if (
            library.SyncStatus is not ("queued" or "running")
            || library.ActiveSyncRunId is null
            || library.ActiveSyncRunCreatedAtUtc is null
        )
        {
            return TypedResults.BadRequest(
                new IngestError(
                    "This library does not have an active queued or running sync to reset."
                )
            );
        }

        var now = DateTimeOffset.UtcNow;
        var reset = await libraries.ResetLibrarySyncStateAsync(orgId, library.Id, now, ct);
        if (reset is null)
        {
            return TypedResults.BadRequest(
                new IngestError(
                    "The active sync changed before it could be reset. Refresh and retry."
                )
            );
        }

        // NOTE: ResetLibrarySyncStateAsync marks the active run Stalled and
        // clears the library lease in one transaction. A null result means the
        // run was not active anymore or could not be transitioned, so the lease
        // remains intact for a later retry/diagnostic pass.
        return TypedResults.Ok(
            new ResetLibraryIngestRunStateResponse(
                reset.Library.SyncStatus ?? "idle",
                reset.Library.NextSyncAt,
                RunMarkedStalled: true
            )
        );
    }
}
