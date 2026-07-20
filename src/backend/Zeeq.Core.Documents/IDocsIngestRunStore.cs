namespace Zeeq.Core.Documents;

/// <summary>
/// Store contract for repository ingest run records.
/// </summary>
public interface IDocsIngestRunStore
{
    /// <summary>Creates a new run record (status <c>Running</c>).</summary>
    Task<DocsIngestRun> CreateAsync(DocsIngestRun run, CancellationToken ct);

    /// <summary>
    /// Gets a run by its composite key. Both <paramref name="id"/> and
    /// <paramref name="createdAtUtc"/> are required — the table is partitioned by
    /// <c>created_at_utc</c>, so supplying it lets Postgres prune to one partition.
    /// </summary>
    Task<DocsIngestRun?> GetAsync(string id, DateTimeOffset createdAtUtc, CancellationToken ct);

    /// <summary>
    /// Finalizes a run: sets terminal status, file counts, failure details, and
    /// <c>completed_at_utc</c>/<c>updated_at_utc</c>.
    /// </summary>
    Task FinalizeAsync(
        string id,
        DateTimeOffset createdAtUtc,
        IngestRunFinalization finalization,
        CancellationToken ct
    );

    /// <summary>
    /// Marks the exact running run as <see cref="IngestRunStatus.Stalled"/>.
    /// Missing rows and rows that are already terminal are ignored.
    /// </summary>
    Task<bool> MarkStalledAsync(
        string id,
        DateTimeOffset createdAtUtc,
        DateTimeOffset completedAtUtc,
        string failureMessage,
        CancellationToken ct
    )
    {
        throw new NotSupportedException();
    }

    /// <summary>Lists the most recent runs for one organization's private sources, newest first.</summary>
    Task<IReadOnlyList<DocsIngestRun>> ListByOrganizationAsync(
        string organizationId,
        int limit,
        CancellationToken ct
    );

    /// <summary>
    /// Lists runs for one private-source library, ordered newest first, with
    /// keyset pagination.
    /// </summary>
    /// <param name="organizationId">Owning organization.</param>
    /// <param name="libraryId">Library to list runs for.</param>
    /// <param name="beforeCreatedAtUtc">
    /// The partition-key value of the last row on the previous page, or
    /// <see langword="null"/> for the first page.
    /// </param>
    /// <param name="beforeId">
    /// The id of the last row on the previous page (tiebreaker within the
    /// same <paramref name="beforeCreatedAtUtc"/>), or <see langword="null"/>
    /// for the first page.
    /// </param>
    /// <param name="limit">Maximum rows to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DocsIngestRun>> ListByLibraryAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    );

    /// <summary>
    /// Lists runs for one public source, ordered newest first, with keyset
    /// pagination. See <see cref="ListByLibraryAsync"/> for the cursor
    /// parameter semantics — every subscribing library's "Sync status" tab
    /// pages through this same shared history.
    /// </summary>
    Task<IReadOnlyList<DocsIngestRun>> ListByPublicSourceAsync(
        string publicSourceId,
        DateTimeOffset? beforeCreatedAtUtc,
        string? beforeId,
        int limit,
        CancellationToken ct
    );
}

/// <summary>
/// Terminal state applied to a run record by <see cref="IDocsIngestRunStore.FinalizeAsync"/>.
/// <see cref="Status"/> should be <see cref="IngestRunStatus.Succeeded"/> when
/// <see cref="FilesFailed"/> is zero, <see cref="IngestRunStatus.Partial"/> when
/// some files failed but the run otherwise completed, and
/// <see cref="IngestRunStatus.Failed"/> for a fatal error.
/// </summary>
public sealed record IngestRunFinalization(
    IngestRunStatus Status,
    int FilesTotal,
    int FilesAdded,
    int FilesUpdated,
    int FilesMoved,
    int FilesSkipped,
    int FilesDeleted,
    int FilesFailed,
    bool AuthFailure,
    string? FailureMessage,
    DateTimeOffset CompletedAtUtc
);
