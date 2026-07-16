namespace Zeeq.Platform.Ingest;

/// <summary>
/// Response returned by a successful manual ingest trigger.
/// </summary>
/// <param name="RunId">
/// The id the eventual <c>DocsIngestRun</c> record will use. No row exists
/// with this id yet — the queued sync message carries it forward so the
/// consumer creates the row with this exact id once it processes the job.
/// </param>
/// <param name="RunCreatedAtUtc">Partition key paired with <paramref name="RunId"/>.</param>
/// <param name="ViewToken">
/// Compact token encoding <paramref name="RunCreatedAtUtc"/> and source kind,
/// for a future run-status viewer endpoint.
/// </param>
public sealed record TriggerIngestRunResponse(
    string RunId,
    DateTimeOffset RunCreatedAtUtc,
    string ViewToken
);

/// <summary>Error payload for ingest trigger endpoints.</summary>
public sealed record IngestError(string Message);

/// <summary>One page of a library's ingest run history, newest first.</summary>
/// <param name="Runs">Runs on this page.</param>
/// <param name="NextCursor">
/// Opaque cursor for the next page, or <see langword="null"/> when this page
/// is the last one (fewer rows than the requested limit were returned).
/// </param>
public sealed record IngestRunPageResponse(IngestRunSummaryResponse[] Runs, string? NextCursor);

/// <summary>Summary of one ingest run for the run-history list.</summary>
public sealed record IngestRunSummaryResponse(
    string Id,
    string Status,
    string Trigger,
    int FilesTotal,
    int FilesAdded,
    int FilesUpdated,
    int FilesMoved,
    int FilesDeleted,
    int FilesFailed,
    bool AuthFailure,
    string? FailureMessage,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc
);
