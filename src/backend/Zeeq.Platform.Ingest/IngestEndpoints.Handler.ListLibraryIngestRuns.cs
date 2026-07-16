using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Lists ingest run history for a library, newest first, cursor-paginated.
/// </summary>
/// <remarks>
/// Branches the same way <see cref="TriggerLibraryIngestHandler"/> does: a
/// public-source-backed library's history is the source's shared run history
/// (every subscribing library's "Sync status" tab pages through the same
/// rows), a private-source library's history is scoped to just that library.
/// </remarks>
public sealed class ListLibraryIngestRunsHandler(
    ILibraryDocumentStore libraries,
    IDocsIngestRunStore runs
) : IEndpointHandler
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    /// <summary>Handles the list library ingest runs request.</summary>
    public async Task<
        Results<Ok<IngestRunPageResponse>, BadRequest<IngestError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        string? cursor,
        int? limit,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var library = await libraries.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        if (library.PublicSourceId is null && library.SourceKind is null)
        {
            return TypedResults.BadRequest(
                new IngestError("This library has no repository source and no ingest run history.")
            );
        }

        DateTimeOffset? beforeCreatedAtUtc = null;
        string? beforeId = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (
                !IngestRunListCursor.TryDecode(
                    cursor,
                    out var decodedCreatedAtUtc,
                    out var decodedId
                )
            )
            {
                return TypedResults.BadRequest(new IngestError("Invalid cursor."));
            }

            beforeCreatedAtUtc = decodedCreatedAtUtc;
            beforeId = decodedId;
        }

        var pageSize = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // Fetch one extra row so we can tell "exactly pageSize rows, no more"
        // apart from "exactly pageSize rows, and there's at least one more" —
        // `page.Count == pageSize` alone can't distinguish those.
        var page = library.PublicSourceId is { } publicSourceId
            ? await runs.ListByPublicSourceAsync(
                publicSourceId,
                beforeCreatedAtUtc,
                beforeId,
                pageSize + 1,
                ct
            )
            : await runs.ListByLibraryAsync(
                orgId,
                library.Id,
                beforeCreatedAtUtc,
                beforeId,
                pageSize + 1,
                ct
            );

        var hasMore = page.Count > pageSize;
        var items = hasMore ? page.Take(pageSize).ToArray() : page;
        var nextCursor = hasMore
            ? IngestRunListCursor.Encode(items[^1].CreatedAtUtc, items[^1].Id)
            : null;

        return TypedResults.Ok(new IngestRunPageResponse([.. items.Select(ToSummary)], nextCursor));
    }

    private static IngestRunSummaryResponse ToSummary(DocsIngestRun run) =>
        new(
            run.Id,
            run.Status.ToString(),
            run.Trigger.ToString(),
            run.FilesTotal,
            run.FilesAdded,
            run.FilesUpdated,
            run.FilesMoved,
            run.FilesDeleted,
            run.FilesFailed,
            run.AuthFailure,
            run.FailureMessage,
            run.StartedAtUtc,
            run.CompletedAtUtc
        );
}
