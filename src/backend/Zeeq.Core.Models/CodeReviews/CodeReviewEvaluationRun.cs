namespace Zeeq.Core.Models;

/// <summary>
/// One admitted reviewer backtesting run over an immutable dataset manifest.
/// </summary>
/// <remarks>
/// The manifest is authoritative: it freezes dataset membership, reference content,
/// repository configuration, reviewer configuration, model descriptors and prompt
/// identity at admission. Case results point at manifest case ids, never at mutable
/// dataset cases, so editing or removing a dataset case cannot rewrite history.
/// Admission, claim, renewal and every fenced write compare the run state, the lease
/// token and the lease expiry inside SQL.
/// </remarks>
public sealed class CodeReviewEvaluationRun : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Optional team context inherited from the repository mapping.</summary>
    public string? TeamId { get; init; }

    /// <summary>Repository mapping the run belongs to.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Dataset the manifest was copied from.</summary>
    public required string DatasetId { get; init; }

    /// <summary>Dataset version copied into the manifest.</summary>
    public required long DatasetVersion { get; init; }

    /// <summary>Reviewer agent the manifest evaluates.</summary>
    public required string AgentId { get; init; }

    /// <summary>Saved revision the manifest was seeded from, when the run used one.</summary>
    public string? SavedRevisionId { get; init; }

    /// <summary>Immutable JSON document freezing every input for this run.</summary>
    public required string ManifestJson { get; set; }

    /// <summary>Authenticated user that requested the run.</summary>
    public required string RequestedByUserId { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public required CodeReviewEvaluationRunState State { get; set; }

    /// <summary>Parent run for a failed-case retry. Null for a root run.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>Client idempotency key, unique within the organization.</summary>
    public required string RequestKey { get; init; }

    /// <summary>Opaque worker ownership token for the live lease.</summary>
    public string? LeaseToken { get; set; }

    /// <summary>UTC time after which the lease may be fenced.</summary>
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    /// <summary>Set when a user requests cancellation. New case starts are rejected once set.</summary>
    public DateTimeOffset? CancelRequestedAtUtc { get; set; }

    /// <summary>UTC time a worker first claimed the run.</summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>UTC time the run reached a terminal state.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>Number of cases copied into the manifest.</summary>
    public int CaseCount { get; set; }

    /// <summary>Number of cases that reached a successful grade.</summary>
    public int GradedCaseCount { get; set; }

    /// <summary>Number of cases skipped before any provider call.</summary>
    public int SkippedCaseCount { get; set; }

    /// <summary>Number of cases that failed a stage.</summary>
    public int FailedCaseCount { get; set; }

    /// <summary>Number of cases whose reviewer expectation set is unlabelled.</summary>
    public int UnlabelledCaseCount { get; set; }

    /// <summary>Expected reference findings that the judge matched.</summary>
    public int MatchedReferenceCount { get; set; }

    /// <summary>Expected reference findings that the judge reported as missed.</summary>
    public int MissedReferenceCount { get; set; }

    /// <summary>Total expected reference findings across graded labelled cases.</summary>
    public int ExpectedReferenceCount { get; set; }

    /// <summary>Candidate findings the judge reported as additional and unverified.</summary>
    public int AdditionalFindingCount { get; set; }

    /// <summary>Stable failure code for terminal error states.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable failure detail for diagnostics. Never contains prompt or source content.</summary>
    public string? FailureMessage { get; set; }
}
