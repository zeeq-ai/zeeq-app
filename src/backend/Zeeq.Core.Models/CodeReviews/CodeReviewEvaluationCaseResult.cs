namespace Zeeq.Core.Models;

/// <summary>
/// Per-case execution result of one evaluation run.
/// </summary>
/// <remarks>
/// Rows key off <see cref="ManifestCaseId"/>, not the mutable dataset case, because the
/// run manifest is authoritative. A terminal successful grade is immutable: retries
/// create a child run and never overwrite parent results. Candidate output must be
/// persisted before grading starts so a judge failure never loses reviewer output.
/// </remarks>
public sealed class CodeReviewEvaluationCaseResult
    : MutableDomainEntityBase,
        IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Run this case result belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>Manifest case id copied at admission.</summary>
    public required string ManifestCaseId { get; init; }

    /// <summary>Provider pull request number from the manifest.</summary>
    public required int PullRequestNumber { get; init; }

    /// <summary>Current execution phase.</summary>
    public required CodeReviewEvaluationCasePhase Phase { get; set; }

    /// <summary>Terminal outcome, or <see cref="CodeReviewEvaluationCaseOutcome.None"/> while running.</summary>
    public required CodeReviewEvaluationCaseOutcome Outcome { get; set; }

    /// <summary>Server-owned candidate output artifact, written before grading.</summary>
    public string? CandidateStorageUri { get; set; }

    /// <summary>SHA-256 of the candidate artifact bytes.</summary>
    public string? CandidateSha256 { get; set; }

    /// <summary>Byte length of the candidate artifact.</summary>
    public int? CandidateByteCount { get; set; }

    /// <summary>Number of candidate findings parsed from the candidate artifact.</summary>
    public int CandidateFindingCount { get; set; }

    /// <summary>Expected reference findings the judge matched.</summary>
    public int MatchedReferenceCount { get; set; }

    /// <summary>Expected reference findings the judge reported as missed.</summary>
    public int MissedReferenceCount { get; set; }

    /// <summary>Expected reference findings considered by the judge.</summary>
    public int ExpectedReferenceCount { get; set; }

    /// <summary>Candidate findings the judge reported as additional and unverified.</summary>
    public int AdditionalFindingCount { get; set; }

    /// <summary>Parsed candidate findings as a JSON array (no reference content).</summary>
    public string? CandidateFindingsJson { get; set; }

    /// <summary>Judge match assignments as a JSON array of reference-id to candidate-id pairs.</summary>
    public string? JudgeMatchesJson { get; set; }

    /// <summary>Short per-match and per-miss judge reasons as a JSON array.</summary>
    public string? JudgeReasonsJson { get; set; }

    /// <summary>Reviewer provenance JSON: admitted and actual model/build identity.</summary>
    public string? ReviewerProvenanceJson { get; set; }

    /// <summary>Judge provenance JSON: admitted and actual model/build identity.</summary>
    public string? JudgeProvenanceJson { get; set; }

    /// <summary>Reviewer stage input tokens, when reported.</summary>
    public long? ReviewerInputTokens { get; set; }

    /// <summary>Reviewer stage output tokens, when reported.</summary>
    public long? ReviewerOutputTokens { get; set; }

    /// <summary>Judge stage input tokens, when reported.</summary>
    public long? JudgeInputTokens { get; set; }

    /// <summary>Judge stage output tokens, when reported.</summary>
    public long? JudgeOutputTokens { get; set; }

    /// <summary>Estimated API-rate USD cost for this case, when priceable.</summary>
    public decimal? EstimatedCostUsd { get; set; }

    /// <summary>Stable failure or skip code, for example <c>NoFilesInScope</c> or <c>JudgeInvalidJson</c>.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Short failure detail for diagnostics. Never contains prompt, source or reference content.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>UTC time the case started reviewing.</summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>UTC time the case reached a terminal outcome.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
