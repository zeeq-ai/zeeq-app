namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Shared render-kind values that select comment-section behavior across the publisher
/// (<see cref="CodeReviewRequestService"/>, <see cref="CodeReviewRunRequestedHandler"/>) and every
/// consumer (<see cref="GitHubCommentWriteRequestedHandler"/> and the <c>IGitHubCommentSectionRenderer</c>
/// implementations).
/// </summary>
/// <remarks>
/// These are plain <c>string</c> constants, not an enum, because <see cref="GitHubCommentWriteRequested.Kind"/>
/// is a wire value on a message serialized over the queue — constants preserve that format while
/// still centralizing spelling. NOTE: a future step could promote this to a small value object
/// (e.g. a readonly struct wrapping the string with an implicit conversion) for stronger typing
/// than a bare <c>string</c> parameter on every renderer; deferred for now to keep this change a
/// pure de-duplication with no signature changes.
/// </remarks>
public static class GitHubCommentKinds
{
    /// <summary>The review was accepted and the reviewer workflow was queued.</summary>
    public const string Queued = "queued";

    /// <summary>The PR is a draft; Zeeq will review it once it is ready.</summary>
    public const string DraftPrompt = "draft_prompt";

    /// <summary>The triggering GitHub event did not require a new review.</summary>
    public const string Ignored = "ignored";

    /// <summary>A review was already active for this PR; the new request was dropped.</summary>
    public const string AlreadyRunning = "already_running";

    /// <summary>The PR's remaining review budget is exhausted.</summary>
    public const string AllowanceExhausted = "allowance_exhausted";

    /// <summary>The review run failed before producing findings.</summary>
    public const string ReviewFailed = "review_failed";

    /// <summary>The review completed and findings are available.</summary>
    public const string ReviewCompleted = "review_completed";

    /// <summary>A stub/placeholder review completed (no real agent execution).</summary>
    public const string StubReviewCompleted = "stub_review_completed";

    /// <summary>The review completed but no configured reviewer agents were activated.</summary>
    public const string NoAgentsActivated = "no_agents_activated";

    /// <summary>The PR was closed or merged.</summary>
    public const string Closed = "closed";
}
