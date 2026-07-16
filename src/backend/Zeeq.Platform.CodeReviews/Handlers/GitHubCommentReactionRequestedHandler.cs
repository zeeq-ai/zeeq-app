using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Consumes immediate reaction work and writes the acknowledgement reaction to GitHub.
/// </summary>
/// <remarks>
/// Reactions are intentionally lighter than rendered comments. They do not use
/// anchors, leases, or a Zeeq dedupe table because GitHub already treats a
/// repeated reaction by the same app/user as idempotent. The handler treats
/// created and already-present outcomes as success. Validation failures are
/// logged and acknowledged because this path should never block review or
/// comment-rendering work.
/// </remarks>
[ConfigureConsumer<GitHubCommentReactionRequested>(
    "github.comment.reaction.worker",
    noOfPerformers: 4, // 16
    bufferSize: 10,
    visibleTimeoutSeconds: 60,
    pollIntervalMilliseconds: 50
)]
public sealed partial class GitHubCommentReactionRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    IGitHubCommentReactionClientFactory reactionClients,
    ILogger<GitHubCommentReactionRequestedHandler> logger
) : ZeeqMessageHandler<GitHubCommentReactionRequested>(deadLetterWriter)
{
    /// <summary>
    /// Adds one acknowledgement reaction to one GitHub comment.
    /// </summary>
    /// <remarks>
    /// The only non-success outcome swallowed here is validation failure. Other
    /// provider failures are allowed to escape so the normal Zeeq message
    /// retry and dead-letter policy can handle transient GitHub or auth issues.
    /// </remarks>
    protected override async Task<GitHubCommentReactionRequested> HandleMessageAsync(
        GitHubCommentReactionRequested message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);
        var client = await reactionClients.CreateForOrganizationAsync(
            message.OrganizationId,
            cancellationToken
        );
        var outcome = await client.AddReactionAsync(
            message.OwnerQualifiedRepoName,
            message.Target,
            message.ReactionContent,
            cancellationToken
        );

        GitHubReactionTelemetry.ReactionWrites.Add(
            1,
            new KeyValuePair<string, object?>("outcome", FormatOutcome(outcome)),
            new KeyValuePair<string, object?>("target.kind", message.Target.Kind.ToString())
        );
        activity?.SetTag("github.reaction.outcome", FormatOutcome(outcome));
        activity?.SetTag("github.reaction.target_kind", message.Target.Kind.ToString());

        if (outcome == GitHubCommentReactionWriteOutcome.ValidationFailed)
        {
            LogValidationNoOp(
                logger,
                message.SignalId,
                message.OwnerQualifiedRepoName,
                message.Target.Kind.ToString(),
                message.Target.CommentId
            );
            return message;
        }

        LogReactionWritten(
            logger,
            message.SignalId,
            message.OwnerQualifiedRepoName,
            message.Target.Kind.ToString(),
            message.Target.CommentId,
            FormatOutcome(outcome)
        );
        return message;
    }

    private static Activity? StartActivity(GitHubCommentReactionRequested message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);
        var activity = ZeeqTelemetry.Tracer.StartActivity(
            "github.reaction.write",
            ActivityKind.Consumer,
            parentContext
        );

        activity?.SetTag("github.delivery_id", message.SignalId);
        activity?.SetTag("github.repo", message.OwnerQualifiedRepoName);
        activity?.SetTag("github.comment_id", message.Target.CommentId);
        activity?.SetTag("pull_request.number", message.PullRequestNumber);

        return activity;
    }

    private static string FormatOutcome(GitHubCommentReactionWriteOutcome outcome) =>
        outcome switch
        {
            GitHubCommentReactionWriteOutcome.Created => "created",
            GitHubCommentReactionWriteOutcome.AlreadyExists => "deduplicated",
            GitHubCommentReactionWriteOutcome.ValidationFailed => "validation_no_op",
            _ => "unknown",
        };

    [LoggerMessage(
        EventId = 6060,
        Level = LogLevel.Information,
        Message = "Wrote GitHub reaction for signal {SignalId} on {Repository} {TargetKind} comment {CommentId} with outcome {Outcome}."
    )]
    private static partial void LogReactionWritten(
        ILogger logger,
        string signalId,
        string repository,
        string targetKind,
        long commentId,
        string outcome
    );

    [LoggerMessage(
        EventId = 6061,
        Level = LogLevel.Warning,
        Message = "GitHub reaction validation no-op for signal {SignalId} on {Repository} {TargetKind} comment {CommentId}."
    )]
    private static partial void LogValidationNoOp(
        ILogger logger,
        string signalId,
        string repository,
        string targetKind,
        long commentId
    );
}
