using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for per-case execution results inside one evaluation run.
/// </summary>
/// <remarks>
/// Every write is fenced by run state <c>Running</c>, the owning lease token, an unexpired
/// lease and the expected case phase, all checked in SQL. A candidate artifact is persisted
/// before grading starts, so a judge failure never loses reviewer output. Terminal outcomes
/// are immutable; retries happen in a child run.
/// </remarks>
public interface ICodeReviewEvaluationCaseResultStore
{
    /// <summary>Moves a case from pending to reviewing.</summary>
    Task<CodeReviewEvaluationCaseResult> StartReviewAsync(
        CodeReviewEvaluationCaseStart request,
        CancellationToken cancellationToken
    );

    /// <summary>Moves a case from reviewing to reviewed and records its candidate artifact.</summary>
    Task<CodeReviewEvaluationCaseResult> RecordCandidateAsync(
        CodeReviewEvaluationCaseCandidate request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Records a terminal reviewed outcome for a case whose reviewer set is unlabelled.
    /// </summary>
    /// <remarks>
    /// The candidate output is preserved exactly as in <see cref="RecordCandidateAsync"/> so
    /// it can later seed reference findings. No judge call happens for this outcome.
    /// </remarks>
    Task<CodeReviewEvaluationCaseResult> RecordUnlabelledAsync(
        CodeReviewEvaluationCaseUnlabelled request,
        CancellationToken cancellationToken
    );

    /// <summary>Moves a case into grading, optionally reusing a parent candidate artifact.</summary>
    /// <remarks>
    /// Without a reused candidate the transition is allowed only from <c>Reviewed</c>. With
    /// one, it is allowed from <c>Pending</c> or <c>Reviewed</c>, and the parent run, parent
    /// case, artifact hash and parent <c>JudgeFailed</c> outcome are all verified first.
    /// </remarks>
    Task<CodeReviewEvaluationCaseResult> StartGradingAsync(
        CodeReviewEvaluationCaseGrading request,
        CancellationToken cancellationToken
    );

    /// <summary>Moves a case from grading to graded with binary match counts.</summary>
    Task<CodeReviewEvaluationCaseResult> RecordGradeAsync(
        CodeReviewEvaluationCaseGrade request,
        CancellationToken cancellationToken
    );

    /// <summary>Records a terminal failure without touching a persisted candidate artifact.</summary>
    Task<CodeReviewEvaluationCaseResult> RecordFailureAsync(
        CodeReviewEvaluationCaseFailure request,
        CancellationToken cancellationToken
    );

    /// <summary>Records a terminal skip that happened before any provider call.</summary>
    Task<CodeReviewEvaluationCaseResult> RecordSkipAsync(
        CodeReviewEvaluationCaseSkip request,
        CancellationToken cancellationToken
    );

    /// <summary>Records a terminal cancellation while preserving any candidate artifact.</summary>
    Task<CodeReviewEvaluationCaseResult> RecordCancelledAsync(
        string organizationId,
        string runId,
        string caseId,
        string leaseToken,
        CancellationToken cancellationToken
    );

    /// <summary>Lists every case result for one run.</summary>
    Task<IReadOnlyList<CodeReviewEvaluationCaseResult>> ListForRunAsync(
        string organizationId,
        string runId,
        CancellationToken cancellationToken
    );

    /// <summary>Finds one case result by manifest case id.</summary>
    Task<CodeReviewEvaluationCaseResult?> FindAsync(
        string organizationId,
        string runId,
        string caseId,
        CancellationToken cancellationToken
    );
}

/// <summary>Fenced request that starts the reviewer stage for one case.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being executed.</param>
/// <param name="CaseId">Manifest case id.</param>
/// <param name="LeaseToken">Owning lease token.</param>
public sealed record CodeReviewEvaluationCaseStart(
    string OrganizationId,
    string RunId,
    string CaseId,
    string LeaseToken
);

/// <summary>Fenced request that records persisted candidate output.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being executed.</param>
/// <param name="CaseId">Manifest case id.</param>
/// <param name="LeaseToken">Owning lease token.</param>
/// <param name="CandidateArtifact">Persisted candidate artifact reference.</param>
/// <param name="CandidateFindingCount">Number of parsed candidate findings.</param>
/// <param name="CandidateFindingsJson">Parsed candidate findings JSON.</param>
/// <param name="ReviewerProvenanceJson">Reviewer provenance JSON.</param>
/// <param name="InputTokens">Reviewer input tokens, or null when the provider did not report usage.</param>
/// <param name="OutputTokens">Reviewer output tokens, or null when the provider did not report usage.</param>
/// <param name="EstimatedCostUsd">Estimated reviewer cost, when priceable.</param>
public sealed record CodeReviewEvaluationCaseCandidate(
    string OrganizationId,
    string RunId,
    string CaseId,
    string LeaseToken,
    CodeReviewEvaluationArtifact CandidateArtifact,
    int CandidateFindingCount,
    string CandidateFindingsJson,
    string ReviewerProvenanceJson,
    long? InputTokens,
    long? OutputTokens,
    decimal? EstimatedCostUsd
);

/// <summary>Fenced request that records a terminal unlabelled outcome with persisted output.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being executed.</param>
/// <param name="CaseId">Manifest case id.</param>
/// <param name="LeaseToken">Owning lease token.</param>
/// <param name="CandidateArtifact">Persisted candidate artifact reference.</param>
/// <param name="CandidateFindingCount">Number of parsed candidate findings.</param>
/// <param name="CandidateFindingsJson">Parsed candidate findings JSON.</param>
/// <param name="ReviewerProvenanceJson">Reviewer provenance JSON.</param>
/// <param name="InputTokens">Reviewer input tokens, or null when the provider did not report usage.</param>
/// <param name="OutputTokens">Reviewer output tokens, or null when the provider did not report usage.</param>
/// <param name="EstimatedCostUsd">Estimated reviewer cost, when priceable.</param>
public sealed record CodeReviewEvaluationCaseUnlabelled(
    string OrganizationId,
    string RunId,
    string CaseId,
    string LeaseToken,
    CodeReviewEvaluationArtifact CandidateArtifact,
    int CandidateFindingCount,
    string CandidateFindingsJson,
    string ReviewerProvenanceJson,
    long? InputTokens,
    long? OutputTokens,
    decimal? EstimatedCostUsd
);

/// <summary>Fenced request that starts grading for one case.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being executed.</param>
/// <param name="CaseId">Manifest case id.</param>
/// <param name="LeaseToken">Owning lease token.</param>
/// <param name="ReusedCandidate">Parent candidate artifact for a judge-only retry, otherwise null.</param>
public sealed record CodeReviewEvaluationCaseGrading(
    string OrganizationId,
    string RunId,
    string CaseId,
    string LeaseToken,
    CodeReviewEvaluationArtifact? ReusedCandidate
);

/// <summary>Fenced request that records a binary grade.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being executed.</param>
/// <param name="CaseId">Manifest case id.</param>
/// <param name="LeaseToken">Owning lease token.</param>
/// <param name="MatchedReferenceCount">Expected findings the judge matched.</param>
/// <param name="MissedReferenceCount">Expected findings the judge reported as missed.</param>
/// <param name="ExpectedReferenceCount">Expected findings considered by the judge.</param>
/// <param name="AdditionalFindingCount">Candidate findings reported as additional and unverified.</param>
/// <param name="JudgeMatchesJson">One-to-one reference to candidate assignment JSON.</param>
/// <param name="JudgeReasonsJson">Short reason JSON.</param>
/// <param name="JudgeProvenanceJson">Judge provenance JSON.</param>
/// <param name="InputTokens">Judge input tokens, or null when the provider did not report usage.</param>
/// <param name="OutputTokens">Judge output tokens, or null when the provider did not report usage.</param>
/// <param name="EstimatedCostUsd">Estimated judge cost, when priceable.</param>
public sealed record CodeReviewEvaluationCaseGrade(
    string OrganizationId,
    string RunId,
    string CaseId,
    string LeaseToken,
    int MatchedReferenceCount,
    int MissedReferenceCount,
    int ExpectedReferenceCount,
    int AdditionalFindingCount,
    string JudgeMatchesJson,
    string JudgeReasonsJson,
    string JudgeProvenanceJson,
    long? InputTokens,
    long? OutputTokens,
    decimal? EstimatedCostUsd
);

/// <summary>Fenced request that records a terminal case failure.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being executed.</param>
/// <param name="CaseId">Manifest case id.</param>
/// <param name="LeaseToken">Owning lease token.</param>
/// <param name="Outcome">Terminal outcome. Only reviewer and judge failures are accepted.</param>
/// <param name="ErrorCode">Stable failure code.</param>
/// <param name="FailureMessage">Short failure detail. Never contains prompts, source or reference content.</param>
public sealed record CodeReviewEvaluationCaseFailure(
    string OrganizationId,
    string RunId,
    string CaseId,
    string LeaseToken,
    CodeReviewEvaluationCaseOutcome Outcome,
    string ErrorCode,
    string FailureMessage
);

/// <summary>Fenced request that records a terminal pre-execution skip.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RunId">Run being executed.</param>
/// <param name="CaseId">Manifest case id.</param>
/// <param name="LeaseToken">Owning lease token.</param>
/// <param name="SkipReason">Literal <c>NoFilesInScope</c> or <c>NoAgentActivation</c>.</param>
public sealed record CodeReviewEvaluationCaseSkip(
    string OrganizationId,
    string RunId,
    string CaseId,
    string LeaseToken,
    string SkipReason
);
