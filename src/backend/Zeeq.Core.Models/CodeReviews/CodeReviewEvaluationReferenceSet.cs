namespace Zeeq.Core.Models;

/// <summary>
/// Review state of one reviewer's expectation set for one captured pull request.
/// </summary>
/// <remarks>
/// This state is independent from execution state. A failed, skipped or unlabelled
/// case never becomes <see cref="ReviewedClean"/> implicitly; an operator must choose
/// that state explicitly.
/// </remarks>
public enum CodeReviewEvaluationReferenceState
{
    /// <summary>No expectation recorded yet. The case has no grade and is not counted as clean.</summary>
    Unlabelled,

    /// <summary>The reviewer is expected to find the listed findings.</summary>
    Expected,

    /// <summary>The reviewer is expected to report no findings for this case.</summary>
    ReviewedClean,
}

/// <summary>
/// How one reference finding entered the dataset.
/// </summary>
public enum CodeReviewEvaluationReferenceOrigin
{
    /// <summary>Typed in by an operator.</summary>
    Manual,

    /// <summary>Adopted from a persisted evaluation case result.</summary>
    RunFinding,

    /// <summary>Copied from a historical production review. The source review is never modified.</summary>
    HistoricalReview,
}

/// <summary>
/// Severity carried by a reference finding.
/// </summary>
/// <remarks>
/// This is a storage and contract enum owned by the domain layer; the execution
/// layer maps it from <c>CodeReviewFindingLevel</c> at the API boundary.
/// </remarks>
public enum CodeReviewEvaluationFindingSeverity
{
    /// <summary>A blocking correctness, security, or data-loss issue.</summary>
    Critical,

    /// <summary>A serious issue that should be fixed before merge.</summary>
    Major,

    /// <summary>A small correctness, maintainability, or test issue.</summary>
    Minor,

    /// <summary>A non-blocking improvement suggestion.</summary>
    Suggestion,

    /// <summary>An informational comment.</summary>
    Comment,
}

/// <summary>
/// Expected findings for one reviewer on one dataset case.
/// </summary>
/// <remarks>
/// Reference sets are keyed by the stable reviewer agent id, never by editable
/// facet text, so renaming a facet cannot leak expectations across reviewers.
/// Content is copied into a run manifest at admission; later edits never change
/// an admitted run.
/// </remarks>
/// <param name="ReviewerAgentId">Stable reviewer agent id that owns this expectation set.</param>
/// <param name="State">Expectation state for this reviewer and case.</param>
/// <param name="Findings">Expected findings. Empty is valid for <see cref="CodeReviewEvaluationReferenceState.ReviewedClean"/>.</param>
public sealed record CodeReviewEvaluationReferenceSet(
    string ReviewerAgentId,
    CodeReviewEvaluationReferenceState State,
    IReadOnlyList<CodeReviewEvaluationReferenceFinding> Findings
)
{
    /// <summary>Reference set that exists but has no expectation yet.</summary>
    public static CodeReviewEvaluationReferenceSet Unlabelled(string reviewerAgentId) =>
        new(reviewerAgentId, CodeReviewEvaluationReferenceState.Unlabelled, []);
}

/// <summary>
/// One expected finding inside a reviewer's reference set.
/// </summary>
/// <param name="Id">Stable identifier used by the judge to report matches.</param>
/// <param name="Title">Short expectation title shown in the reference editor.</param>
/// <param name="Summary">What the reviewer should report, at most 4000 characters.</param>
/// <param name="Severity">Expected severity. Severity does not affect matching in v1.</param>
/// <param name="File">Repository-relative file path the finding is expected in.</param>
/// <param name="Line">Optional expected line number. Location helps comparison but never forces a match.</param>
/// <param name="Origin">How this finding entered the dataset.</param>
/// <param name="SourceRunId">Evaluation run the finding was adopted from, when applicable.</param>
/// <param name="SourceFindingId">Candidate finding id inside <paramref name="SourceRunId"/>, when applicable.</param>
/// <param name="ApprovedByUserId">User who approved the reference into the dataset.</param>
/// <param name="ApprovedAtUtc">Time the reference was approved.</param>
public sealed record CodeReviewEvaluationReferenceFinding(
    string Id,
    string Title,
    string Summary,
    CodeReviewEvaluationFindingSeverity Severity,
    string File,
    int? Line,
    CodeReviewEvaluationReferenceOrigin Origin,
    string? SourceRunId,
    string? SourceFindingId,
    string ApprovedByUserId,
    DateTimeOffset ApprovedAtUtc
);
