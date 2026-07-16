namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider-neutral source for pull request data needed by a code review run.
/// </summary>
public interface ICodeReviewPullRequestSource
{
    /// <summary>
    /// Loads pull request metadata, changed files, and developer feedback for one run request.
    /// </summary>
    Task<CodeReviewPullRequestSnapshot> GetPullRequestAsync(
        CodeReviewRunRequested message,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Source-neutral pull request snapshot used after all provider I/O has completed.
/// </summary>
/// <param name="Title">Current pull request title.</param>
/// <param name="Body">Current pull request description.</param>
/// <param name="Files">Changed files visible to the review source.</param>
/// <param name="DeveloperFeedbackComments">Filtered human feedback to include in the prompt.</param>
public sealed record CodeReviewPullRequestSnapshot(
    string Title,
    string Body,
    IReadOnlyList<CodeReviewFileSnapshot> Files,
    IReadOnlyList<CodeReviewDeveloperFeedbackComment> DeveloperFeedbackComments
);
