using Zeeq.Core.Common;
using Zeeq.Platform.CodeReviews;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.PullRequestReviewThread;
using PullRequestPayload = Octokit.Webhooks.Models.PullRequestEvent.PullRequest;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Handles pull-request lifecycle and review-state webhook deliveries.
/// </summary>
/// <remarks>
/// Pull-request events eventually drive repository lookup, idempotent delivery
/// claiming, PR state persistence, and review workflow publication. Review and
/// thread events are currently contextual pass-through events until the review
/// UX needs them.
/// </remarks>
public partial class ZeeqGitHubWebhookEventProcessor
{
    /// <summary>
    /// Acknowledges PR lifecycle events until queue-backed PR adapters are added.
    /// </summary>
    /// <remarks>
    /// This is one of the primary actionable webhook types. Later slices will
    /// resolve repository mappings, claim the GitHub delivery id, persist PR
    /// state, and publish review workflow messages. For this ingress slice, the
    /// handler is a traced deferred no-op.
    /// </remarks>
    protected override ValueTask ProcessPullRequestWebhookAsync(
        WebhookHeaders headers,
        PullRequestEvent pullRequestEvent,
        PullRequestAction action,
        CancellationToken cancellationToken = default
    ) => HandlePullRequestAsync(headers, pullRequestEvent, action, cancellationToken);

    /// <summary>
    /// Acknowledges submitted, edited, and dismissed PR review events.
    /// </summary>
    /// <remarks>
    /// Review events are useful context for future review-state reconciliation,
    /// but they do not yet drive queue work in this slice.
    /// </remarks>
    protected override ValueTask ProcessPullRequestReviewWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewEvent pullRequestReviewEvent,
        PullRequestReviewAction action,
        CancellationToken cancellationToken = default
    ) => HandlePullRequestReviewPassThrough(headers, pullRequestReviewEvent, action);

    /// <summary>
    /// Acknowledges PR review-thread resolve/unresolve events.
    /// </summary>
    /// <remarks>
    /// Thread state can matter for future review UX, but phase one only needs
    /// the delivery to be visible and acknowledged.
    /// </remarks>
    protected override ValueTask ProcessPullRequestReviewThreadWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewThreadEvent pullRequestReviewThreadEvent,
        PullRequestReviewThreadAction action,
        CancellationToken cancellationToken = default
    ) => HandlePullRequestReviewThreadPassThrough(headers, pullRequestReviewThreadEvent, action);

    /// <summary>
    /// Records a pull-request lifecycle event as deferred queue work.
    /// </summary>
    private ValueTask HandlePullRequestAsync(
        WebhookHeaders headers,
        PullRequestEvent pullRequestEvent,
        PullRequestAction action,
        CancellationToken cancellationToken
    ) =>
        HandleActionableAsync(
            headers,
            pullRequestEvent,
            FormatPullRequestAction(action),
            "pull_request",
            (metadata, repository, traceContext) =>
                CreatePullRequestMessage(
                    metadata,
                    repository,
                    traceContext,
                    pullRequestEvent.PullRequest
                ),
            cancellationToken
        );

    /// <summary>
    /// Creates the provider-neutral queue payload for PR lifecycle work.
    /// </summary>
    /// <remarks>
    /// The message intentionally carries current PR state from the webhook
    /// payload. The next queue handler can persist/update Zeeq records without
    /// making an outbound GitHub call on the public ingress request.
    /// </remarks>
    private static GitHubPullRequestWebhookReceived CreatePullRequestMessage(
        GitHubWebhookMetadata metadata,
        GitHubWebhookRepositoryMapping repository,
        ZeeqTraceContext traceContext,
        PullRequestPayload? pullRequest
    ) =>
        new()
        {
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            GitHubDeliveryId = metadata.DeliveryId,
            GitHubEvent = metadata.EventName,
            GitHubAction = metadata.Action,
            GitHubInstallationId = metadata.InstallationIdAsLong,
            TraceContext = traceContext,
            PullRequestNumber = pullRequest?.Number ?? 0,
            PullRequestNodeId = pullRequest?.NodeId,
            Title = pullRequest?.Title,
            HtmlUrl = pullRequest?.HtmlUrl,
            IsDraft = pullRequest?.Draft ?? false,
            State = pullRequest?.State?.ToString(),
            HeadRef = pullRequest?.Head?.Ref,
            BaseRef = pullRequest?.Base?.Ref,
            HeadSha = pullRequest?.Head?.Sha,
            AuthorLogin = pullRequest?.User?.Login,
        };

    /// <summary>
    /// Records a pull-request review event as an acknowledged no-op for this phase.
    /// </summary>
    private ValueTask HandlePullRequestReviewPassThrough(
        WebhookHeaders headers,
        PullRequestReviewEvent pullRequestReviewEvent,
        PullRequestReviewAction action
    ) =>
        HandlePassThrough(
            headers,
            pullRequestReviewEvent,
            FormatPullRequestReviewAction(action),
            "pull_request_review"
        );

    /// <summary>
    /// Records a pull-request review-thread event as an acknowledged no-op for this phase.
    /// </summary>
    private ValueTask HandlePullRequestReviewThreadPassThrough(
        WebhookHeaders headers,
        PullRequestReviewThreadEvent pullRequestReviewThreadEvent,
        PullRequestReviewThreadAction action
    ) =>
        HandlePassThrough(
            headers,
            pullRequestReviewThreadEvent,
            FormatPullRequestReviewThreadAction(action),
            "pull_request_review_thread"
        );

    /// <summary>
    /// Converts Octokit pull-request action values to GitHub payload strings.
    /// </summary>
    private static string FormatPullRequestAction(PullRequestAction action) =>
        action switch
        {
            var value when value == PullRequestAction.Assigned => PullRequestActionValue.Assigned,
            var value when value == PullRequestAction.AutoMergeDisabled =>
                PullRequestActionValue.AutoMergeDisabled,
            var value when value == PullRequestAction.AutoMergeEnabled =>
                PullRequestActionValue.AutoMergeEnabled,
            var value when value == PullRequestAction.Closed => PullRequestActionValue.Closed,
            var value when value == PullRequestAction.ConvertedToDraft =>
                PullRequestActionValue.ConvertedToDraft,
            var value when value == PullRequestAction.Dequeued => PullRequestActionValue.Dequeued,
            var value when value == PullRequestAction.Demilestoned =>
                PullRequestActionValue.Demilestoned,
            var value when value == PullRequestAction.Edited => PullRequestActionValue.Edited,
            var value when value == PullRequestAction.Labeled => PullRequestActionValue.Labeled,
            var value when value == PullRequestAction.Locked => PullRequestActionValue.Locked,
            var value when value == PullRequestAction.Milestoned =>
                PullRequestActionValue.Milestoned,
            var value when value == PullRequestAction.Opened => PullRequestActionValue.Opened,
            var value when value == PullRequestAction.Enqueued => PullRequestActionValue.Enqueued,
            var value when value == PullRequestAction.ReadyForReview =>
                PullRequestActionValue.ReadyForReview,
            var value when value == PullRequestAction.Reopened => PullRequestActionValue.Reopened,
            var value when value == PullRequestAction.ReviewRequestRemoved =>
                PullRequestActionValue.ReviewRequestRemoved,
            var value when value == PullRequestAction.ReviewRequested =>
                PullRequestActionValue.ReviewRequested,
            var value when value == PullRequestAction.Synchronize =>
                PullRequestActionValue.Synchronize,
            var value when value == PullRequestAction.Unassigned =>
                PullRequestActionValue.Unassigned,
            var value when value == PullRequestAction.Unlabeled => PullRequestActionValue.Unlabeled,
            var value when value == PullRequestAction.Unlocked => PullRequestActionValue.Unlocked,
            _ => string.Empty,
        };

    /// <summary>
    /// Converts Octokit PR-review action values to GitHub payload strings.
    /// </summary>
    private static string FormatPullRequestReviewAction(PullRequestReviewAction action) =>
        action switch
        {
            var value when value == PullRequestReviewAction.Dismissed =>
                PullRequestReviewActionValue.Dismissed,
            var value when value == PullRequestReviewAction.Edited =>
                PullRequestReviewActionValue.Edited,
            var value when value == PullRequestReviewAction.Submitted =>
                PullRequestReviewActionValue.Submitted,
            _ => string.Empty,
        };

    /// <summary>
    /// Converts Octokit PR-review-thread action values to GitHub payload strings.
    /// </summary>
    private static string FormatPullRequestReviewThreadAction(
        PullRequestReviewThreadAction action
    ) =>
        action switch
        {
            var value when value == PullRequestReviewThreadAction.Resolved =>
                PullRequestReviewThreadActionValue.Resolved,
            var value when value == PullRequestReviewThreadAction.Unresolved =>
                PullRequestReviewThreadActionValue.Unresolved,
            _ => string.Empty,
        };
}
