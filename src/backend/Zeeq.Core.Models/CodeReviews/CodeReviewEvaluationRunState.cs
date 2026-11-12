namespace Zeeq.Core.Models;

/// <summary>
/// Lifecycle state of one evaluation run.
/// </summary>
/// <remarks>
/// Admission allows at most one <see cref="Queued"/> or <see cref="Running"/> run per
/// organization. <see cref="DispatchFailed"/> is a durable terminal state for a queued
/// run whose publish failed; it frees admission without losing the row.
/// </remarks>
public enum CodeReviewEvaluationRunState
{
    /// <summary>Persisted and awaiting a consumer.</summary>
    Queued,

    /// <summary>Owned by a worker holding a live lease.</summary>
    Running,

    /// <summary>Every manifested case reached a successful grade.</summary>
    Completed,

    /// <summary>Finished with at least one skipped or failed case, or with unlabelled cases.</summary>
    Incomplete,

    /// <summary>Cancelled on request; finished results are preserved.</summary>
    Cancelled,

    /// <summary>Lease expired and the run was fenced; retry creates a child run.</summary>
    Abandoned,

    /// <summary>Queue publish failed before any consumer claimed the run.</summary>
    DispatchFailed,
}

/// <summary>
/// Execution phase of one manifested case.
/// </summary>
/// <remarks>
/// Phases advance monotonically while the run lease is valid. Terminal case outcomes
/// live in <see cref="CodeReviewEvaluationCaseOutcome"/>, so a failed case is still in a
/// readable phase rather than losing its progress.
/// </remarks>
public enum CodeReviewEvaluationCasePhase
{
    /// <summary>Waiting for a free case pipeline.</summary>
    Pending,

    /// <summary>The reviewer is running.</summary>
    Reviewing,

    /// <summary>Candidate output was persisted and the reviewer stage finished.</summary>
    Reviewed,

    /// <summary>The judge is running against the persisted candidate output.</summary>
    Grading,

    /// <summary>The case reached a successful binary grade.</summary>
    Graded,
}

/// <summary>
/// Terminal outcome of one manifested case.
/// </summary>
public enum CodeReviewEvaluationCaseOutcome
{
    /// <summary>No terminal outcome yet.</summary>
    None,

    /// <summary>Graded successfully; matched and additional counts are authoritative.</summary>
    Graded,

    /// <summary>The reviewer stage failed. No candidate output exists.</summary>
    ReviewFailed,

    /// <summary>The judge failed or returned invalid output. Candidate output is preserved.</summary>
    JudgeFailed,

    /// <summary>
    /// The reviewer finished and its output was persisted, but the reviewer's reference
    /// set is unlabelled, so the case has no grade. Output is retained for later adoption.
    /// </summary>
    Unlabelled,

    /// <summary>The case was skipped before any provider call.</summary>
    Skipped,

    /// <summary>The case was cancelled before it finished.</summary>
    Cancelled,
}
