using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Side-effect-free aggregate passed into prompt construction and reviewer execution.
/// </summary>
/// <remarks>
/// The runner builds this context only after database, GitHub, storage, and agent-resolution I/O has finished.
/// Pure downstream components can then be tested without provider or persistence fakes.
/// </remarks>
public sealed record CodeReviewExecutionContext(
    CodeReviewRecord Review,
    PullRequestRecord PullRequest,
    CodeReviewPullRequestSnapshot Snapshot,
    CodeRepositoryReviewConfiguration RepositoryConfiguration,
    IReadOnlyList<CodeReviewerRuntimeAgent> Agents,
    IReadOnlyList<CodeReviewFileSnapshot> InScopeFiles,
    IReadOnlyList<CodeReviewFileSnapshot> OutOfScopeFiles
)
{
    /// <summary>
    /// Narrows the durable execution context to the fields consumed by prompt construction.
    /// </summary>
    public CodeReviewPromptInput ToPromptInput(IReadOnlyList<string> libraryNames) =>
        new(
            Snapshot.Title,
            Snapshot.Body,
            Snapshot.DeveloperFeedbackComments,
            InScopeFiles,
            OutOfScopeFiles,
            libraryNames,
            RepositoryConfiguration.SharedPromptFragment
        );
}
