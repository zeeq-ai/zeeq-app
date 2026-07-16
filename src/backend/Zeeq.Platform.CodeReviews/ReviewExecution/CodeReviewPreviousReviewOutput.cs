namespace Zeeq.Platform.CodeReviews;

// See: src/backend/Zeeq.Platform.CodeReviews/ReviewExecution/CodeReviewAgentExecutor.cs

/// <summary>
/// Lightweight representation of one previous review finding for prompt construction.
/// </summary>
public sealed record CodeReviewPreviousFinding(
    string Summary,
    string Details,
    string File,
    CodeReviewFindingLevel Level
);

/// <summary>
/// Lightweight representation of one previous review output for prompt construction.
/// </summary>
public sealed record CodeReviewPreviousReview(
    string Facet,
    string Summary,
    IReadOnlyList<CodeReviewPreviousFinding> Findings
);
