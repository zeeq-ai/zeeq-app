namespace Zeeq.Core.Documents;

/// <summary>
/// A publicly-accessible repository that has been ingested as a shared document source.
/// </summary>
/// <remarks>
/// One row per public repository URL globally. Libraries in any organization can subscribe
/// to a public source via a <see cref="Library.PublicSourceId"/> foreign key and apply
/// per-org path filters. This table holds only public repos — private source metadata
/// lives inline on <see cref="Library"/> to prevent cross-org metadata leakage.
/// </remarks>
public class DocsPublicSource
{
    /// <summary>
    /// Stable public source identifier.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Repository source kind — currently always <see cref="RepositorySourceKind.Public"/> for
    /// rows in this table.
    /// </summary>
    public RepositorySourceKind Kind { get; init; }

    /// <summary>
    /// Canonical repository URL, unique globally.
    /// </summary>
    public string RepoUrl { get; init; } = null!;

    /// <summary>
    /// Human-readable source name derived from the repository.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Source-suggested default include globs for new subscribing libraries.
    /// Advisory only; the per-library filter overrides this when set.
    /// </summary>
    public string[] DefaultIncludeFilters { get; init; } = [];

    /// <summary>
    /// Source-suggested default exclude globs for new subscribing libraries.
    /// Advisory only; the per-library filter overrides this when set.
    /// </summary>
    public string[] DefaultExcludeFilters { get; init; } = [];

    /// <summary>
    /// Last time this source was successfully synced.
    /// </summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>
    /// Current sync lifecycle state: <c>idle</c>, <c>queued</c>, <c>running</c>, or <c>quarantined</c>.
    /// </summary>
    public string SyncStatus { get; set; } = "idle";

    /// <summary>
    /// Next scheduled sync time, set after a run completes. The scheduler only dispatches
    /// sources where <see cref="SyncStatus"/> = <c>idle</c> and this timestamp is in the past.
    /// </summary>
    public DateTimeOffset? NextSyncAt { get; set; }

    /// <summary>
    /// Visibility status: <c>active</c> (served to orgs), <c>quarantined</c> (upstream went private).
    /// Quarantined sources are frozen — documents remain but are not served.
    /// </summary>
    public string Status { get; set; } = "active";

    /// <summary>
    /// Last 5 manual trigger timestamps used for rate limiting. Stored as a compact array
    /// on the row so the rate-limit check is a single read-modify-write.
    /// </summary>
    public DateTimeOffset[] ManualTriggerHistory { get; set; } = [];

    /// <summary>
    /// Timestamp when the source was registered.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the source was last modified.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
