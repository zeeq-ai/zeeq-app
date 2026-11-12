using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for evaluation run admission, ownership and lifecycle transitions.
/// </summary>
/// <remarks>
/// Admission and every ownership transition take the organization advisory lock before
/// row locks, in a fixed order, so concurrent admits, cancels, claims and abandons
/// serialize instead of interleaving. The partial unique index on organization for
/// queued/running rows backs the advisory lock as a durable invariant.
/// No transaction in this store spans a provider or storage call.
/// </remarks>
public interface ICodeReviewEvaluationRunStore
{
    /// <summary>
    /// Admits one run and freezes its manifest under the dataset row lock.
    /// </summary>
    Task<CodeReviewEvaluationRunAdmissionResult> AdmitAsync(
        CodeReviewEvaluationRunAdmission request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Admits a failed-case retry as a new child run.
    /// </summary>
    /// <remarks>
    /// The child manifest is copied from the parent's immutable manifest and restricted to
    /// cases that failed or were interrupted. Successfully graded parent cases are never
    /// rerun and the parent report is never mutated.
    /// </remarks>
    Task<CodeReviewEvaluationRunAdmissionResult> AdmitRetryAsync(
        CodeReviewEvaluationRunRetryAdmission request,
        CancellationToken cancellationToken
    );

    /// <summary>Finds one run with its parsed manifest.</summary>
    Task<CodeReviewEvaluationRunDetail?> FindAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    );

    /// <summary>Finds one run by client idempotency key.</summary>
    Task<CodeReviewEvaluationRunDetail?> FindByRequestKeyAsync(
        string organizationId,
        string requestKey,
        CancellationToken cancellationToken
    );

    /// <summary>Lists runs for one dataset, newest first.</summary>
    Task<CodeReviewEvaluationRunPage> ListAsync(
        CodeReviewEvaluationRunQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>Claims a queued run with a new lease token.</summary>
    Task<CodeReviewEvaluationRunClaimResult> TryClaimAsync(
        string organizationId,
        string runId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Extends a live lease and reports whether cancellation was requested.
    /// </summary>
    /// <remarks>
    /// One call carries both signals so a worker heartbeat never needs a second round-trip
    /// to observe cancellation.
    /// </remarks>
    Task<CodeReviewEvaluationLeaseRenewal> RenewLeaseAsync(
        string organizationId,
        string runId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    );

    /// <summary>Marks a queued run as dispatch-failed and frees admission.</summary>
    Task<CodeReviewEvaluationRun> MarkDispatchFailedAsync(
        string organizationId,
        string runId,
        string errorCode,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Requests cancellation.
    /// </summary>
    /// <remarks>
    /// A queued run with no successful claim becomes terminally cancelled and frees
    /// admission. A running run only records the request: it keeps its lease so finished
    /// case results are preserved, and new case starts are rejected from then on.
    /// </remarks>
    Task<CodeReviewEvaluationRun> RequestCancelAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    );

    /// <summary>Abandons an expired or undispatched run, fences stale writers and frees admission.</summary>
    Task<CodeReviewEvaluationRun> AbandonAsync(
        string organizationId,
        string runId,
        string errorCode,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finalizes a run and recomputes aggregate counts from case results.
    /// </summary>
    /// <remarks>
    /// Caller-supplied counts are ignored. Cases with a terminal outcome keep it; cases
    /// still in flight become cancelled while their persisted candidate artifacts stay
    /// readable.
    /// </remarks>
    Task<CodeReviewEvaluationRun> FinalizeAsync(
        CodeReviewEvaluationRunFinalize request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Input for admitting a root evaluation run.
/// </summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RepositoryId">Repository mapping scope.</param>
/// <param name="DatasetId">Dataset the manifest is copied from.</param>
/// <param name="ExpectedDatasetVersion">Version the caller loaded. Must match the locked dataset row.</param>
/// <param name="AgentId">Reviewer agent evaluated by the run.</param>
/// <param name="SavedRevisionId">Saved revision the configuration came from, when any.</param>
/// <param name="ReviewerConfiguration">Reviewer configuration content to freeze.</param>
/// <param name="ReviewerSelection">Frozen reviewer model descriptor.</param>
/// <param name="JudgeSelection">Frozen judge model descriptor.</param>
/// <param name="JudgeRubricVersion">Judge rubric version.</param>
/// <param name="SharedSystemPrompt">Exact shared system prompt text admitted for this run.</param>
/// <param name="SharedSystemPromptHash">SHA-256 of the admitted shared prompt.</param>
/// <param name="RepositoryConfigurationJson">Effective repository review configuration.</param>
/// <param name="LibraryIds">Library ids mapped to the repository.</param>
/// <param name="BuildVersion">Admitted deployed build identity.</param>
/// <param name="SystemPromptVersion">Admitted system prompt version.</param>
/// <param name="RequestedByUserId">User requesting the run.</param>
/// <param name="RequestKey">Client idempotency key, unique within the organization.</param>
/// <param name="RequestFingerprint">
/// Canonical hash of the request content excluding the idempotency key. A duplicate key with a
/// different fingerprint is a replay of a changed request, not an idempotent retry.
/// </param>
public sealed record CodeReviewEvaluationRunAdmission(
    string OrganizationId,
    string RepositoryId,
    string DatasetId,
    long ExpectedDatasetVersion,
    string AgentId,
    string? SavedRevisionId,
    CodeReviewerAgentRevisionConfiguration ReviewerConfiguration,
    CodeReviewEvaluationModelSelection ReviewerSelection,
    CodeReviewEvaluationModelSelection JudgeSelection,
    string JudgeRubricVersion,
    string SharedSystemPrompt,
    string SharedSystemPromptHash,
    string RepositoryConfigurationJson,
    IReadOnlyList<string> LibraryIds,
    string BuildVersion,
    string SystemPromptVersion,
    string RequestedByUserId,
    string RequestKey,
    string RequestFingerprint
);

/// <summary>
/// Input for admitting a failed-case retry run.
/// </summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="ParentRunId">Parent run whose manifest is copied.</param>
/// <param name="RequestKey">Client idempotency key, unique within the organization.</param>
/// <param name="RequestedByUserId">User requesting the retry.</param>
/// <param name="BuildVersion">Build identity of the retrying deployment.</param>
/// <param name="SystemPromptVersion">System prompt version of the retrying deployment.</param>
/// <param name="RequestFingerprint">Canonical request hash excluding the idempotency key.</param>
public sealed record CodeReviewEvaluationRunRetryAdmission(
    string OrganizationId,
    string ParentRunId,
    string RequestKey,
    string RequestedByUserId,
    string BuildVersion,
    string SystemPromptVersion,
    string RequestFingerprint
);

/// <summary>Admission outcome without exceptions, so handlers map directly to HTTP.</summary>
public enum CodeReviewEvaluationRunAdmissionOutcome
{
    /// <summary>The run was created.</summary>
    Admitted,

    /// <summary>The request key already produced this run; return it unchanged.</summary>
    ExistingRequestKey,

    /// <summary>Another queued or running run exists for the organization.</summary>
    ActiveRunConflict,

    /// <summary>The dataset does not exist under the authorized parent chain.</summary>
    DatasetNotFound,

    /// <summary>The dataset version moved since the caller loaded it.</summary>
    DatasetVersionConflict,

    /// <summary>No manifest case remains to execute, for example a retry with no failed cases.</summary>
    NoRunnableCases,
}

/// <summary>Result of one admission attempt.</summary>
/// <param name="Outcome">Admission outcome.</param>
/// <param name="Run">Created or existing run when available.</param>
/// <param name="ActiveRunId">Conflicting active run id when the outcome is a conflict.</param>
public sealed record CodeReviewEvaluationRunAdmissionResult(
    CodeReviewEvaluationRunAdmissionOutcome Outcome,
    CodeReviewEvaluationRun? Run,
    string? ActiveRunId
);

/// <summary>Claim outcome without exceptions.</summary>
public enum CodeReviewEvaluationRunClaimOutcome
{
    /// <summary>This caller now owns the run with its lease token.</summary>
    Claimed,

    /// <summary>A live lease with the same token already owns the run. No-op.</summary>
    AlreadyOwned,

    /// <summary>The run is not queued, for example already terminal.</summary>
    NotRunnable,

    /// <summary>Another worker holds a live lease.</summary>
    LeaseActive,
}

/// <summary>Result of one claim attempt.</summary>
/// <param name="Outcome">Claim outcome.</param>
/// <param name="Run">Run after a successful claim.</param>
/// <param name="Manifest">Parsed manifest for the claimed run.</param>
public sealed record CodeReviewEvaluationRunClaimResult(
    CodeReviewEvaluationRunClaimOutcome Outcome,
    CodeReviewEvaluationRun? Run,
    CodeReviewEvaluationRunManifest? Manifest
);

/// <summary>Lease renewal result.</summary>
/// <param name="Renewed">True when the lease is still owned and was extended.</param>
/// <param name="CancelRequested">True when the run has a pending cancellation request.</param>
public sealed record CodeReviewEvaluationLeaseRenewal(bool Renewed, bool CancelRequested);

/// <summary>Finalization request. Counts are recomputed from case results.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being finalized.</param>
/// <param name="LeaseToken">Owning lease token.</param>
/// <param name="State">Requested terminal state.</param>
/// <param name="ErrorCode">Optional stable failure code.</param>
/// <param name="FailureMessage">Optional short failure detail. Never contains prompts or source.</param>
public sealed record CodeReviewEvaluationRunFinalize(
    string OrganizationId,
    string RunId,
    string LeaseToken,
    CodeReviewEvaluationRunState State,
    string? ErrorCode,
    string? FailureMessage
);

/// <summary>Run row plus its parsed immutable manifest.</summary>
/// <param name="Run">Persisted run row.</param>
/// <param name="Manifest">Parsed manifest.</param>
public sealed record CodeReviewEvaluationRunDetail(
    CodeReviewEvaluationRun Run,
    CodeReviewEvaluationRunManifest Manifest
);

/// <summary>Filter for run history lists.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RepositoryId">Repository mapping scope.</param>
/// <param name="DatasetId">Optional dataset scope.</param>
/// <param name="CursorId">Optional seek cursor run id.</param>
/// <param name="PageSize">Requested page size.</param>
public sealed record CodeReviewEvaluationRunQuery(
    string OrganizationId,
    string RepositoryId,
    string? DatasetId = null,
    string? CursorId = null,
    int PageSize = 50
);

/// <summary>Page of run history rows.</summary>
/// <param name="Items">Runs, newest first.</param>
/// <param name="NextCursorId">Cursor for the next page, when one exists.</param>
public sealed record CodeReviewEvaluationRunPage(
    IReadOnlyList<CodeReviewEvaluationRun> Items,
    string? NextCursorId
);

/// <summary>
/// Thrown when a fenced write loses ownership of a run or a case phase transition is invalid.
/// </summary>
/// <remarks>
/// This is an expected ownership loss, not an infrastructure failure. Workers stop local
/// work on this exception and never retry the write with the same token.
/// </remarks>
public sealed class CodeReviewEvaluationFencedException : Exception
{
    /// <summary>Creates a fenced-write failure with a stable reason code.</summary>
    public CodeReviewEvaluationFencedException(string reasonCode, string message)
        : base(message) => ReasonCode = reasonCode;

    /// <summary>Stable reason code, for example <c>lease_expired</c> or <c>cancel_requested</c>.</summary>
    public string ReasonCode { get; }
}
