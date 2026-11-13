namespace Zeeq.Core.Models;

/// <summary>
/// One pull request example inside an evaluation dataset.
/// </summary>
/// <remarks>
/// The case owns an immutable snapshot artifact plus the reference expectations for
/// every reviewer in the repository. Case identity is stable across remove and
/// re-add so staged editor state and reference scope survive membership churn.
/// A re-add that performs a fresh capture keeps the id but resets references to
/// <see cref="CodeReviewEvaluationReferenceState.Unlabelled"/>; stale labels are
/// never carried onto changed code.
/// </remarks>
public sealed class CodeReviewEvaluationCase : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Repository mapping this case belongs to.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Dataset this case is a member of.</summary>
    public required string DatasetId { get; init; }

    /// <summary>Provider pull request number captured for this case.</summary>
    public required int PullRequestNumber { get; init; }

    /// <summary>
    /// Pull request title captured with the snapshot.
    /// </summary>
    /// <remarks>
    /// Stored on the case, not read back from the provider, so curation keeps a stable
    /// label after paging, refresh, or a later title change on the live pull request.
    /// </remarks>
    public required string PullRequestTitle { get; set; }

    /// <summary>
    /// Local pull request record id when the pull request is already known to Zeeq.
    /// </summary>
    /// <remarks>
    /// Null is valid: a resolvable pull request can be captured without fabricating a
    /// production review or webhook history for it.
    /// </remarks>
    public string? PullRequestRecordId { get; set; }

    /// <summary>Server-owned storage URI of the immutable capture snapshot artifact.</summary>
    public required string SnapshotStorageUri { get; set; }

    /// <summary>SHA-256 of the snapshot artifact bytes, verified on every read.</summary>
    public required string SnapshotSha256 { get; set; }

    /// <summary>Byte length of the canonical uncompressed snapshot JSON, verified on every read.</summary>
    public int SnapshotByteCount { get; set; }

    /// <summary>Schema version of the snapshot envelope stored in the artifact.</summary>
    public int SnapshotSchemaVersion { get; set; }

    /// <summary>Base commit identity captured for this case.</summary>
    public required string BaseSha { get; set; }

    /// <summary>Head commit identity captured for this case.</summary>
    public required string HeadSha { get; set; }

    /// <summary>Provider pull request state at capture time.</summary>
    public PullRequestState PullRequestState { get; set; }

    /// <summary>UTC time the provider snapshot was captured.</summary>
    public required DateTimeOffset CapturedAtUtc { get; set; }

    /// <summary>Number of changed files included in the snapshot.</summary>
    public int ChangedFileCount { get; set; }

    /// <summary>Number of changed files the provider reported but the snapshot omits.</summary>
    public int OmittedFileCount { get; set; }

    /// <summary>Changed-file count declared by the provider during capture.</summary>
    public int DeclaredChangedFileCount { get; set; }

    /// <summary>Set when the case is soft-removed from the dataset; the row and artifacts remain.</summary>
    public DateTimeOffset? RemovedAtUtc { get; set; }

    /// <summary>
    /// Reference expectations keyed by stable reviewer agent id.
    /// </summary>
    /// <remarks>
    /// Stored as one jsonb document. Save always replaces this collection wholesale,
    /// which keeps concurrent reference edits inside the dataset version check.
    /// </remarks>
    public CodeReviewEvaluationReferenceSet[] ReferenceSets { get; set; } = [];
}
