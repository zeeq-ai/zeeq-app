using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Manages GitHub check-run lifecycle for code-review pull requests.
/// </summary>
/// <remarks>
/// Every method is failure-swallowing: a thrown <see cref="ICheckRunClient"/>
/// call is logged and the caller continues. This ensures the check-run concern
/// never fails a review, webhook, or bypass operation.
/// </remarks>
public interface ICheckRunService
{
    /// <summary>
    /// Posts an <c>in_progress</c> check run on the PR head commit when the
    /// repository has check-run gating enabled.
    /// Called from <see cref="CodeReviewRequestService"/> after a review is queued.
    /// </summary>
    Task MarkPendingAsync(PullRequestRecord pr, CancellationToken ct);

    /// <summary>
    /// Resolves the check run after a review reaches a terminal state.
    /// Called from <see cref="CodeReviewRunRequestedHandler"/>.
    /// </summary>
    /// <param name="review">Completed or errored review record.</param>
    /// <param name="pr">Pull request the review ran against.</param>
    /// <param name="config">Repository check-run gating configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ResolveFromReviewAsync(
        CodeReviewRecord review,
        PullRequestRecord pr,
        CodeRepositoryReviewCheckRunConfiguration config,
        CancellationToken ct
    );

    /// <summary>
    /// Clears a blocking check run by setting its conclusion to <c>success</c>.
    /// Called from the UI endpoint, comment command, or native check-run action.
    /// </summary>
    /// <param name="organizationId">Zeeq organization id.</param>
    /// <param name="repositoryId">Zeeq repository id.</param>
    /// <param name="pullRequestNumber">Provider PR number.</param>
    /// <param name="removedBy">Who requested the bypass: a GitHub login, <c>zeeq-user:{id}</c>, or system marker.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Outcome of the bypass attempt.</returns>
    Task<CheckRunBypassOutcome> BypassAsync(
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        string removedBy,
        CancellationToken ct
    );
}

/// <summary>
/// Outcome of a bypass attempt.
/// </summary>
public enum CheckRunBypassOutcome
{
    /// <summary>
    /// The check was cleared.
    /// </summary>
    Cleared,

    /// <summary>
    /// No check-run state existed for the PR (never posted or already cleared).
    /// </summary>
    NotFound,

    /// <summary>
    /// The PR could not be found.
    /// </summary>
    PrNotFound,

    /// <summary>
    /// The GitHub check-run API call failed. The check state is unchanged.
    /// </summary>
    Failed,
}
