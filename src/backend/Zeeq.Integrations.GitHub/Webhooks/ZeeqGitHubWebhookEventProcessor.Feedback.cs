using Zeeq.Core.Common;
using Zeeq.Platform.CodeReviews;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.PullRequestReviewComment;
using IssueCommentPayload = Octokit.Webhooks.Models.IssueComment;
using IssuePayload = Octokit.Webhooks.Models.Issue;
using ReviewCommentPayload = Octokit.Webhooks.Models.PullRequestReviewComment;
using SimplePullRequestPayload = Octokit.Webhooks.Models.SimplePullRequest;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Handles webhook deliveries that can become Zeeq feedback-command surfaces.
/// </summary>
/// <remarks>
/// GitHub exposes whole-PR conversation comments through issue-comment events
/// and diff comments through pull-request review-comment events. This adapter
/// filters both surfaces to explicit Zeeq command tokens before publishing
/// tenant queue work, so normal GitHub discussion stays outside the feedback
/// pipeline.
/// </remarks>
public partial class ZeeqGitHubWebhookEventProcessor
{
    /// <summary>
    /// Publishes PR issue-comment command events to the tenant queue.
    /// </summary>
    /// <remarks>
    /// GitHub models whole-PR conversation comments as issue comments. Only
    /// comments whose first token matches <see cref="GitHubFeedbackCommandPolicy"/>
    /// become queued work; every other comment is acknowledged as a no-op.
    /// </remarks>
    protected override ValueTask ProcessIssueCommentWebhookAsync(
        WebhookHeaders headers,
        IssueCommentEvent issueCommentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken = default
    ) => HandleIssueCommentAsync(headers, issueCommentEvent, action, cancellationToken);

    /// <summary>
    /// Publishes PR diff-comment command events to the tenant queue.
    /// </summary>
    /// <remarks>
    /// Diff comments are a second command surface for Zeeq feedback. Only
    /// comments whose first token matches <see cref="GitHubFeedbackCommandPolicy"/>
    /// become queued work; every other diff comment is acknowledged as a no-op.
    /// </remarks>
    protected override ValueTask ProcessPullRequestReviewCommentWebhookAsync(
        WebhookHeaders headers,
        PullRequestReviewCommentEvent pullRequestReviewCommentEvent,
        PullRequestReviewCommentAction action,
        CancellationToken cancellationToken = default
    ) =>
        HandlePullRequestReviewCommentAsync(
            headers,
            pullRequestReviewCommentEvent,
            action,
            cancellationToken
        );

    /// <summary>
    /// Routes a PR issue-comment command surface event through repository-gated queue publication.
    /// </summary>
    private async ValueTask HandleIssueCommentAsync(
        WebhookHeaders headers,
        IssueCommentEvent issueCommentEvent,
        IssueCommentAction action,
        CancellationToken cancellationToken
    )
    {
        await HandleActionableAsync(
            headers,
            issueCommentEvent,
            FormatIssueCommentAction(action),
            "issue_comment",
            (metadata, repository, traceContext) =>
                CreateIssueCommentMessage(
                    metadata,
                    repository,
                    traceContext,
                    issueCommentEvent.Issue,
                    issueCommentEvent.Comment
                ),
            cancellationToken
        );

        await HandleActionableAsync(
            headers,
            issueCommentEvent,
            FormatIssueCommentAction(action),
            "issue_comment_bypass",
            (metadata, repository, traceContext) =>
                CreateIssueCommentBypassMessage(
                    metadata,
                    repository,
                    traceContext,
                    issueCommentEvent.Issue,
                    issueCommentEvent.Comment
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// Routes a PR review-comment command surface event through repository-gated queue publication.
    /// </summary>
    private async ValueTask HandlePullRequestReviewCommentAsync(
        WebhookHeaders headers,
        PullRequestReviewCommentEvent pullRequestReviewCommentEvent,
        PullRequestReviewCommentAction action,
        CancellationToken cancellationToken
    )
    {
        await HandleActionableAsync(
            headers,
            pullRequestReviewCommentEvent,
            FormatPullRequestReviewCommentAction(action),
            "pull_request_review_comment",
            (metadata, repository, traceContext) =>
                CreateReviewCommentMessage(
                    metadata,
                    repository,
                    traceContext,
                    pullRequestReviewCommentEvent.PullRequest,
                    pullRequestReviewCommentEvent.Comment
                ),
            cancellationToken
        );

        await HandleActionableAsync(
            headers,
            pullRequestReviewCommentEvent,
            FormatPullRequestReviewCommentAction(action),
            "pull_request_review_comment_bypass",
            (metadata, repository, traceContext) =>
                CreateReviewCommentBypassMessage(
                    metadata,
                    repository,
                    traceContext,
                    pullRequestReviewCommentEvent.PullRequest,
                    pullRequestReviewCommentEvent.Comment
                ),
            cancellationToken
        );
    }

    /// <summary>
    /// Creates a queued issue-comment command-surface message when the issue is a PR.
    /// </summary>
    /// <remarks>
    /// GitHub sends both issue and PR conversation comments through this event.
    /// Zeeq only reacts to PR comments that start with an explicit feedback
    /// command, so plain issue comments and normal PR discussion comments return
    /// null and the processor records an acknowledged no-op.
    /// </remarks>
    private static GitHubIssueCommentWebhookReceived? CreateIssueCommentMessage(
        GitHubWebhookMetadata metadata,
        GitHubWebhookRepositoryMapping repository,
        ZeeqTraceContext traceContext,
        IssuePayload? issue,
        IssueCommentPayload? comment
    )
    {
        if (issue?.PullRequest is null)
        {
            return null;
        }

        if (!GitHubFeedbackCommandPolicy.IsSupportedFeedbackCommand(comment?.Body))
        {
            return null;
        }

        return new()
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
            PullRequestNumber = issue.Number,
            IssueNodeId = issue.NodeId,
            CommentId = comment?.Id ?? 0,
            CommentNodeId = comment?.NodeId,
            CommentBody = comment?.Body,
            CommentAuthorLogin = comment?.User?.Login,
            CommentHtmlUrl = comment?.HtmlUrl,
        };
    }

    /// <summary>
    /// Creates a bypass-check queue message when the issue comment contains a bypass subcommand.
    /// </summary>
    private static GitHubCheckRunBypassRequested? CreateIssueCommentBypassMessage(
        GitHubWebhookMetadata metadata,
        GitHubWebhookRepositoryMapping repository,
        ZeeqTraceContext traceContext,
        IssuePayload? issue,
        IssueCommentPayload? comment
    )
    {
        if (
            issue?.PullRequest is null
            || !GitHubFeedbackCommandPolicy.TryParseCommand(
                comment?.Body,
                out var command
            )
            || command != GitHubFeedbackCommand.BypassCheck
        )
        {
            return null;
        }

        return new()
        {
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = (int)issue.Number,
            CommentAuthorLogin = comment?.User?.Login ?? "unknown",
            GitHubDeliveryId = metadata.DeliveryId,
            TraceContext = traceContext,
        };
    }

    /// <summary>
    /// Creates the queued diff-comment command-surface message.
    /// </summary>
    /// <remarks>
    /// Diff comments are also normal GitHub collaboration text. The same command
    /// policy used for whole-PR issue comments keeps only explicit Zeeq
    /// commands in the queue-backed feedback pipeline.
    /// </remarks>
    private static GitHubPullRequestReviewCommentWebhookReceived? CreateReviewCommentMessage(
        GitHubWebhookMetadata metadata,
        GitHubWebhookRepositoryMapping repository,
        ZeeqTraceContext traceContext,
        SimplePullRequestPayload? pullRequest,
        ReviewCommentPayload? comment
    )
    {
        if (!GitHubFeedbackCommandPolicy.IsSupportedFeedbackCommand(comment?.Body))
        {
            return null;
        }

        return new()
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
            CommentId = comment?.Id ?? 0,
            CommentNodeId = comment?.NodeId,
            CommentBody = comment?.Body,
            CommentAuthorLogin = comment?.User?.Login,
            CommentHtmlUrl = comment?.HtmlUrl,
            Path = comment?.Path,
            CommitId = comment?.CommitId,
            PullRequestReviewId = comment?.PullRequestReviewId ?? 0,
            InReplyToId = comment?.InReplyToId,
        };
    }

    /// <summary>
    /// Creates a bypass-check queue message when the diff comment contains a bypass subcommand.
    /// </summary>
    private static GitHubCheckRunBypassRequested? CreateReviewCommentBypassMessage(
        GitHubWebhookMetadata metadata,
        GitHubWebhookRepositoryMapping repository,
        ZeeqTraceContext traceContext,
        SimplePullRequestPayload? pullRequest,
        ReviewCommentPayload? comment
    )
    {
        if (
            !GitHubFeedbackCommandPolicy.TryParseCommand(
                comment?.Body,
                out var command
            )
            || command != GitHubFeedbackCommand.BypassCheck
        )
        {
            return null;
        }

        if (pullRequest?.Number is null or 0)
        {
            return null;
        }

        return new()
        {
            OrganizationId = repository.OrganizationId,
            TeamId = repository.TeamId,
            RepositoryId = repository.Id,
            OwnerQualifiedRepoName = repository.OwnerQualifiedName,
            PullRequestNumber = (int)pullRequest.Number,
            CommentAuthorLogin = comment?.User?.Login ?? "unknown",
            GitHubDeliveryId = metadata.DeliveryId,
            TraceContext = traceContext,
        };
    }

    /// <summary>
    /// Converts Octokit issue-comment action values to GitHub payload strings.
    /// </summary>
    private static string FormatIssueCommentAction(IssueCommentAction action) =>
        action switch
        {
            var value when value == IssueCommentAction.Created => IssueCommentActionValue.Created,
            var value when value == IssueCommentAction.Deleted => IssueCommentActionValue.Deleted,
            var value when value == IssueCommentAction.Edited => IssueCommentActionValue.Edited,
            var value when value == IssueCommentAction.Pinned => IssueCommentActionValue.Pinned,
            var value when value == IssueCommentAction.Unpinned => IssueCommentActionValue.Unpinned,
            _ => string.Empty,
        };

    /// <summary>
    /// Converts Octokit PR-review-comment action values to GitHub payload strings.
    /// </summary>
    private static string FormatPullRequestReviewCommentAction(
        PullRequestReviewCommentAction action
    ) =>
        action switch
        {
            var value when value == PullRequestReviewCommentAction.Created =>
                PullRequestReviewCommentActionValue.Created,
            var value when value == PullRequestReviewCommentAction.Deleted =>
                PullRequestReviewCommentActionValue.Deleted,
            var value when value == PullRequestReviewCommentAction.Edited =>
                PullRequestReviewCommentActionValue.Edited,
            _ => string.Empty,
        };
}
