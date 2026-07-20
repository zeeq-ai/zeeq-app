namespace Zeeq.Core.Documents;

/// <summary>
/// A named collection of documents owned by an organization and optionally a team.
/// </summary>
/// <remarks>
/// A library may be organization-scoped or team-scoped. Documents are always assigned
/// to a library. A library may be linked to a public source (<see cref="PublicSourceId"/>)
/// or configured as a private-source library (<see cref="SourceKind"/> is non-null).
/// </remarks>
public class Library
{
    /// <summary>
    /// Stable library identifier.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Organization that owns the library.
    /// </summary>
    public string OrganizationId { get; init; } = null!;

    /// <summary>
    /// Optional team that owns the library within the organization.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Human-readable library name, unique within the organization.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Optional human-readable library description.
    /// </summary>
    public string? Description { get; set; }

    // ─── Public source linkage (set when the library subscribes to a public repo) ───

    /// <summary>
    /// Foreign key to <see cref="DocsPublicSource"/>. Non-null means this library is backed
    /// by a public repository source. Mutually exclusive with <see cref="SourceKind"/>.
    /// </summary>
    public string? PublicSourceId { get; init; }

    /// <summary>
    /// Org-level include path globs override. When non-empty, overrides the public source's
    /// <see cref="DocsPublicSource.DefaultIncludeFilters"/> or the private source's defaults.
    /// One entry per discrete folder path.
    /// </summary>
    public string[] IncludeFilters { get; init; } = [];

    /// <summary>
    /// Org-level exclude path globs override. When non-empty, overrides the public source's
    /// <see cref="DocsPublicSource.DefaultExcludeFilters"/> or the private source's defaults.
    /// </summary>
    public string[] ExcludeFilters { get; init; } = [];

    // ─── Private source metadata (set when this library ingests a private repo) ───

    /// <summary>
    /// Non-null when this library is a private-source library. The kind of repository
    /// source (e.g. "GitHub"). Mutually exclusive with <see cref="PublicSourceId"/>.
    /// </summary>
    public string? SourceKind { get; init; }

    /// <summary>
    /// Private repository URL. Set alongside <see cref="SourceKind"/>.
    /// </summary>
    public string? SourceRepoUrl { get; init; }

    /// <summary>
    /// Last time this private source was successfully synced.
    /// </summary>
    public DateTimeOffset? SourceSyncedAt { get; set; }

    /// <summary>
    /// Source-suggested default include globs for this private source.
    /// Advisory only. Overridden by <see cref="IncludeFilters"/> when non-empty.
    /// </summary>
    public string[] SourceDefaultIncludeFilters { get; init; } = [];

    /// <summary>
    /// Source-suggested default exclude globs for this private source.
    /// Advisory only. Overridden by <see cref="ExcludeFilters"/> when non-empty.
    /// </summary>
    public string[] SourceDefaultExcludeFilters { get; init; } = [];

    // ─── Sync lifecycle state ───

    /// <summary>
    /// Current sync lifecycle state: <c>idle</c>, <c>queued</c>, <c>running</c>, or <c>paused</c>.
    /// Only set when the library has a source (public or private).
    /// </summary>
    public string? SyncStatus { get; set; }

    /// <summary>
    /// Next scheduled sync time for source-backed libraries. The scheduler only dispatches
    /// sources where <see cref="SyncStatus"/> = <c>idle</c> and this timestamp is in the past.
    /// </summary>
    public DateTimeOffset? NextSyncAt { get; set; }

    /// <summary>
    /// Run id for the sync currently queued or running for this private-source library.
    /// </summary>
    public string? ActiveSyncRunId { get; set; }

    /// <summary>
    /// Partition key paired with <see cref="ActiveSyncRunId"/> for the current sync run.
    /// </summary>
    public DateTimeOffset? ActiveSyncRunCreatedAtUtc { get; set; }

    /// <summary>
    /// Time this library most recently entered the queued sync state.
    /// </summary>
    public DateTimeOffset? SyncQueuedAtUtc { get; set; }

    /// <summary>
    /// Time this library most recently entered the running sync state.
    /// </summary>
    public DateTimeOffset? SyncStartedAtUtc { get; set; }

    /// <summary>
    /// Last 5 manual trigger timestamps used for rate limiting.
    /// </summary>
    public DateTimeOffset[] ManualTriggerHistory { get; set; } = [];

    /// <summary>
    /// Timestamp when the library was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the library was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
