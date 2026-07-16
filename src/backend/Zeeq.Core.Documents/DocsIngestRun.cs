namespace Zeeq.Core.Documents;

/// <summary>
/// Durable record of one repository ingest execution.
/// </summary>
/// <remarks>
/// The table is range-partitioned by <see cref="CreatedAtUtc"/> and uses
/// <c>(id, created_at_utc)</c> as its primary key. PostgreSQL requires partition keys
/// to participate in unique constraints. The pg_partman extension manages child partition
/// creation with 14-day intervals. Run records are the durable audit trail for every
/// repository sync — counts, timings, auth failures, and the root OTEL trace id.
/// </remarks>
public class DocsIngestRun
{
    /// <summary>
    /// UUIDv7 run identifier — also the <c>sync_run_id</c> stamped on documents.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Partition key — UTC timestamp when the run was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// <see cref="RepositorySourceKind.Public"/> or <see cref="RepositorySourceKind.Private"/>.
    /// </summary>
    public RepositorySourceKind SourceKind { get; init; }

    /// <summary>
    /// Canonical repository URL being ingested.
    /// </summary>
    public string RepoUrl { get; init; } = null!;

    /// <summary>
    /// Public source reference — set when <see cref="SourceKind"/> is <see cref="RepositorySourceKind.Public"/>.
    /// </summary>
    public string? PublicSourceId { get; init; }

    /// <summary>
    /// Owning organization — set when <see cref="SourceKind"/> is <see cref="RepositorySourceKind.Private"/>.
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// Owning library — set when <see cref="SourceKind"/> is <see cref="RepositorySourceKind.Private"/>.
    /// </summary>
    public string? LibraryId { get; init; }

    /// <summary>
    /// What initiated the run: <see cref="IngestTriggerReason.Scheduled"/> or
    /// <see cref="IngestTriggerReason.Manual"/>.
    /// </summary>
    public IngestTriggerReason Trigger { get; init; }

    /// <summary>
    /// Current run status: Running, Succeeded, Partial, or Failed.
    /// </summary>
    public IngestRunStatus Status { get; set; }

    /// <summary>
    /// Root OTEL trace identifier for this run. Links the run record to its telemetry.
    /// </summary>
    public string? RootTraceId { get; set; }

    /// <summary>
    /// Total files enumerated in the repository.
    /// </summary>
    public int FilesTotal { get; set; }

    /// <summary>
    /// Files newly added to the document store.
    /// </summary>
    public int FilesAdded { get; set; }

    /// <summary>
    /// Files whose content changed and were updated.
    /// </summary>
    public int FilesUpdated { get; set; }

    /// <summary>
    /// Files detected at a new path via content-hash dedup.
    /// </summary>
    public int FilesMoved { get; set; }

    /// <summary>
    /// Files whose content hash matched — no write needed.
    /// </summary>
    public int FilesSkipped { get; set; }

    /// <summary>
    /// Files removed by the deletion sweep after a clean pass.
    /// </summary>
    public int FilesDeleted { get; set; }

    /// <summary>
    /// Files that failed to read, parse, or upsert.
    /// </summary>
    public int FilesFailed { get; set; }

    /// <summary>
    /// True when an authentication or access failure prevented the clone or token mint.
    /// </summary>
    public bool AuthFailure { get; set; }

    /// <summary>
    /// Human-readable failure reason for failed or partial runs.
    /// </summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// UTC timestamp when the run started work.
    /// </summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the run reached a terminal state.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the run record was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
