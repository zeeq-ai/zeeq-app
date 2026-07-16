using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Consumes PR issue-comment webhook messages and emits lightweight reaction work.
/// </summary>
/// <remarks>
/// This is the first feedback-command slice. The webhook adapter should only
/// publish comments that match <see cref="GitHubFeedbackCommandPolicy"/>, and
/// this handler checks the same policy again so replayed or manually published
/// messages cannot create reactions for normal GitHub discussion. Later command
/// execution can build on the same message after immediate acknowledgement has
/// proven the path end to end.
/// </remarks>
[ConfigureConsumer<GitHubIssueCommentWebhookReceived>(
    "github.webhook.issue-comment.feedback",
    // IGitHubWebhookTenantMessage (: ITenantMessage) already fans this out
    // across every tenant-tier bucket (20 by default) — noOfPerformers
    // multiplies that, it doesn't add to it.
    noOfPerformers: 1,
    bufferSize: 10,
    visibleTimeoutSeconds: 60,
    pollIntervalMilliseconds: 250
)]
public sealed partial class GitHubIssueCommentWebhookReceivedHandler(
    IDeadLetterWriter deadLetterWriter,
    IZeeqMessagePublisher publisher,
    CodeReviewSettings settings,
    ILogger<GitHubIssueCommentWebhookReceivedHandler> logger
) : ZeeqMessageHandler<GitHubIssueCommentWebhookReceived>(deadLetterWriter)
{
    /// <summary>
    /// Publishes reaction work for a created PR issue-comment when it is safe to acknowledge.
    /// </summary>
    protected override async Task<GitHubIssueCommentWebhookReceived> HandleMessageAsync(
        GitHubIssueCommentWebhookReceived message,
        CancellationToken cancellationToken
    )
    {
        using var activity = GitHubFeedbackReactionHandlerSupport.StartActivity(
            message.GitHubEvent,
            message.GitHubAction,
            message.TraceContext,
            message.GitHubDeliveryId,
            message.OwnerQualifiedRepoName,
            message.PullRequestNumber
        );

        if (!GitHubFeedbackReactionHandlerSupport.ShouldPublishReaction(message, settings))
        {
            LogIssueCommentIgnored(
                logger,
                message.GitHubDeliveryId,
                message.GitHubAction,
                message.CommentAuthorLogin ?? string.Empty
            );
            return message;
        }

        await publisher.PublishAsync(
            new GitHubCommentReactionRequested
            {
                OrganizationId = message.OrganizationId,
                TeamId = message.TeamId,
                RepositoryId = message.RepositoryId,
                OwnerQualifiedRepoName = message.OwnerQualifiedRepoName,
                PullRequestNumber = ToPullRequestNumber(message.PullRequestNumber),
                Target = new(GitHubCommentReactionTargetKind.IssueComment, message.CommentId),
                ReactionContent = GitHubCommentReactionContent.PlusOne,
                SignalId = message.GitHubDeliveryId,
                TraceContext = message.TraceContext,
            },
            cancellationToken
        );

        LogIssueCommentReactionPublished(logger, message.GitHubDeliveryId, message.CommentId);
        return message;
    }

    private static int ToPullRequestNumber(long pullRequestNumber) =>
        checked((int)pullRequestNumber);

    [LoggerMessage(
        EventId = 6040,
        Level = LogLevel.Debug,
        Message = "Ignored GitHub issue comment feedback delivery {DeliveryId} with action {Action} by {AuthorLogin}."
    )]
    private static partial void LogIssueCommentIgnored(
        ILogger logger,
        string deliveryId,
        string action,
        string authorLogin
    );

    [LoggerMessage(
        EventId = 6041,
        Level = LogLevel.Information,
        Message = "Published GitHub issue comment reaction work for delivery {DeliveryId} and comment {CommentId}."
    )]
    private static partial void LogIssueCommentReactionPublished(
        ILogger logger,
        string deliveryId,
        long commentId
    );
}

/// <summary>
/// Consumes PR review-comment webhook messages and emits lightweight reaction work.
/// </summary>
/// <remarks>
/// Diff comments use a different GitHub reaction endpoint than issue-thread
/// comments, so this handler publishes the same reaction message with a
/// <see cref="GitHubCommentReactionTargetKind.PullRequestReviewComment"/> target.
/// The command policy is enforced before reaction publication so review-thread
/// discussion only enters the immediate lane when it starts with an explicit
/// Zeeq command.
/// </remarks>
[ConfigureConsumer<GitHubPullRequestReviewCommentWebhookReceived>(
    "github.webhook.review-comment.feedback",
    // Same tenant-bucket fan-out caveat as GitHubIssueCommentWebhookReceived
    // above — the stale "// 4" comment reflects an older, smaller bucket-count
    // total; today's default totals 20, so noOfPerformers: 2 was actually
    // producing 40 real performer threads for this handler alone.
    noOfPerformers: 1,
    bufferSize: 10,
    visibleTimeoutSeconds: 60,
    pollIntervalMilliseconds: 100
)]
public sealed partial class GitHubPullRequestReviewCommentWebhookReceivedHandler(
    IDeadLetterWriter deadLetterWriter,
    IZeeqMessagePublisher publisher,
    CodeReviewSettings settings,
    ILogger<GitHubPullRequestReviewCommentWebhookReceivedHandler> logger
) : ZeeqMessageHandler<GitHubPullRequestReviewCommentWebhookReceived>(deadLetterWriter)
{
    /// <summary>
    /// Publishes reaction work for a created PR review-comment when it is safe to acknowledge.
    /// </summary>
    protected override async Task<GitHubPullRequestReviewCommentWebhookReceived> HandleMessageAsync(
        GitHubPullRequestReviewCommentWebhookReceived message,
        CancellationToken cancellationToken
    )
    {
        using var activity = GitHubFeedbackReactionHandlerSupport.StartActivity(
            message.GitHubEvent,
            message.GitHubAction,
            message.TraceContext,
            message.GitHubDeliveryId,
            message.OwnerQualifiedRepoName,
            message.PullRequestNumber
        );

        if (!GitHubFeedbackReactionHandlerSupport.ShouldPublishReaction(message, settings))
        {
            LogReviewCommentIgnored(
                logger,
                message.GitHubDeliveryId,
                message.GitHubAction,
                message.CommentAuthorLogin ?? string.Empty
            );
            return message;
        }

        await publisher.PublishAsync(
            new GitHubCommentReactionRequested
            {
                OrganizationId = message.OrganizationId,
                TeamId = message.TeamId,
                RepositoryId = message.RepositoryId,
                OwnerQualifiedRepoName = message.OwnerQualifiedRepoName,
                PullRequestNumber = ToPullRequestNumber(message.PullRequestNumber),
                Target = new(
                    GitHubCommentReactionTargetKind.PullRequestReviewComment,
                    message.CommentId
                ),
                ReactionContent = GitHubCommentReactionContent.PlusOne,
                SignalId = message.GitHubDeliveryId,
                TraceContext = message.TraceContext,
            },
            cancellationToken
        );

        LogReviewCommentReactionPublished(logger, message.GitHubDeliveryId, message.CommentId);
        return message;
    }

    private static int ToPullRequestNumber(long pullRequestNumber) =>
        checked((int)pullRequestNumber);

    [LoggerMessage(
        EventId = 6050,
        Level = LogLevel.Debug,
        Message = "Ignored GitHub review comment feedback delivery {DeliveryId} with action {Action} by {AuthorLogin}."
    )]
    private static partial void LogReviewCommentIgnored(
        ILogger logger,
        string deliveryId,
        string action,
        string authorLogin
    );

    [LoggerMessage(
        EventId = 6051,
        Level = LogLevel.Information,
        Message = "Published GitHub review comment reaction work for delivery {DeliveryId} and comment {CommentId}."
    )]
    private static partial void LogReviewCommentReactionPublished(
        ILogger logger,
        string deliveryId,
        long commentId
    );
}

file static class GitHubFeedbackReactionHandlerSupport
{
    public static Activity? StartActivity(
        string eventName,
        string action,
        ZeeqTraceContext traceContext,
        string deliveryId,
        string ownerQualifiedRepoName,
        long pullRequestNumber
    )
    {
        ZeeqTelemetry.TryParseTraceContext(traceContext, out var parentContext);
        var activity = ZeeqTelemetry.Tracer.StartActivity(
            CreateActivityName(eventName, action),
            ActivityKind.Consumer,
            parentContext
        );

        activity?.SetTag("github.event", eventName);
        activity?.SetTag("github.action", action);
        activity?.SetTag("github.delivery_id", deliveryId);
        activity?.SetTag("github.repo", ownerQualifiedRepoName);
        activity?.SetTag("pull_request.number", pullRequestNumber);

        return activity;
    }

    private static string CreateActivityName(string eventName, string action) =>
        string.IsNullOrWhiteSpace(action)
            ? $"github.webhook.{eventName}.feedback"
            : $"github.webhook.{eventName}.{action}.feedback";

    public static bool ShouldPublishReaction(
        GitHubIssueCommentWebhookReceived message,
        CodeReviewSettings settings
    ) =>
        IsCreatedAction(message.GitHubAction)
        && IsUsableComment(message.CommentId, message.CommentBody)
        && GitHubFeedbackCommandPolicy.IsSupportedFeedbackCommand(message.CommentBody)
        && IsUserAuthored(message.CommentAuthorLogin, settings);

    public static bool ShouldPublishReaction(
        GitHubPullRequestReviewCommentWebhookReceived message,
        CodeReviewSettings settings
    ) =>
        IsCreatedAction(message.GitHubAction)
        && IsUsableComment(message.CommentId, message.CommentBody)
        && GitHubFeedbackCommandPolicy.IsSupportedFeedbackCommand(message.CommentBody)
        && IsUserAuthored(message.CommentAuthorLogin, settings);

    private static bool IsCreatedAction(string action) =>
        string.Equals(action, "created", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsableComment(long commentId, string? commentBody) =>
        commentId > 0 && !string.IsNullOrWhiteSpace(commentBody);

    private static bool IsUserAuthored(string? authorLogin, CodeReviewSettings settings)
    {
        if (string.IsNullOrWhiteSpace(authorLogin))
        {
            return false;
        }

        var login = authorLogin.Trim();
        if (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (login.Contains("zeeq", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.Equals(login, settings.AgentIdentity, StringComparison.OrdinalIgnoreCase);
    }
}
