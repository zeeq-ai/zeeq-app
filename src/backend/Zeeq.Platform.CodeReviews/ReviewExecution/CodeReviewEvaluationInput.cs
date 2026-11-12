using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Verified admission-time input for evaluation. Reference answers are intentionally absent.</summary>
public sealed record CodeReviewEvaluationInput(
    CodeReviewPullRequestSnapshot Snapshot,
    CodeRepositoryReviewConfiguration Configuration,
    IReadOnlyList<CodeReviewerRuntimeAgent> Reviewers,
    IReadOnlyList<string> LibraryIds
);
