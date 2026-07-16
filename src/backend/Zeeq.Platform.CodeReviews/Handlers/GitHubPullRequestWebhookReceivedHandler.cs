using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles queued GitHub pull-request lifecycle webhook messages.
/// </summary>
/// <remarks>
/// Webhook HTTP ingress publishes <see cref="GitHubPullRequestWebhookReceived"/>
/// only after it has validated the GitHub signature and resolved a configured
/// Zeeq repository. This handler keeps the webhook-specific workflow
/// responsibilities: claim the GitHub delivery id, re-check that the repository
/// mapping is still enabled, and hand the normalized review request to
/// <see cref="CodeReviewRequestService"/>.
///
/// The implementation intentionally does not call GitHub. GitHub API mutation
/// and review execution happen in later queued handlers so webhook processing
/// stays retryable and cheap.
/// </remarks>
[ConfigureConsumer<GitHubPullRequestWebhookReceived>(
    "github.webhook.pull-request.workflow",
    // IGitHubWebhookTenantMessage (: ITenantMessage) already fans this out
    // across every tenant-tier bucket (20 by default) — noOfPerformers
    // multiplies that, it doesn't add to it. The stale "// 4" comment reflects
    // an older, smaller bucket-count total; today's default totals 20, so the
    // prior noOfPerformers: 2 was actually producing 40 real performer threads.
    noOfPerformers: 1,
    bufferSize: 10,
    visibleTimeoutSeconds: 60,
    pollIntervalMilliseconds: 50
)]
public sealed partial class GitHubPullRequestWebhookReceivedHandler(
    IDeadLetterWriter deadLetterWriter,
    IGitHubWebhookDeliveryStore deliveries,
    ICodeRepositoryStore codeRepositories,
    CodeReviewRequestService reviewRequests,
    IAgentTelemetryPullRequestLinker telemetryLinker,
    ILogger<GitHubPullRequestWebhookReceivedHandler> logger
) : ZeeqMessageHandler<GitHubPullRequestWebhookReceived>(deadLetterWriter)
{
    /// <summary>
    /// Processes one accepted pull-request webhook message.
    /// </summary>
    /// <remarks>
    /// The order is intentional: claim the GitHub delivery first, re-check that
    /// the repository mapping is still actionable, then delegate the shared PR
    /// upsert, lifecycle gates, budget gate, active-review lock, review row,
    /// immediate comment signal, and run request publication to
    /// <see cref="CodeReviewRequestService"/>. That keeps duplicate deliveries
    /// from creating duplicate reviews while giving manual review requests a
    /// single durable path to reuse.
    /// </remarks>
    protected override async Task<GitHubPullRequestWebhookReceived> HandleMessageAsync(
        GitHubPullRequestWebhookReceived message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);
        LogPullRequestWorkflowReceived(
            logger,
            message.OrganizationId,
            message.OwnerQualifiedRepoName,
            message.PullRequestNumber,
            message.GitHubAction,
            message.IsDraft,
            message.State
        );

        var claim = await deliveries.ClaimAsync(CreateDelivery(message), cancellationToken);
        if (claim != WebhookDeliveryClaimResult.Claimed)
        {
            LogDuplicateDelivery(logger, message.GitHubDeliveryId, claim.ToString());

            return message;
        }

        if (!await IsRepositoryStillEnabledAsync(message, cancellationToken))
        {
            ZeeqTelemetry.AddEvent(
                [
                    ("github.delivery_id", message.GitHubDeliveryId),
                    ("github.repo", message.OwnerQualifiedRepoName),
                    ("gate.reason", "repository_disabled"),
                ],
                eventName: "pr.review_gated"
            );

            LogRepositoryGateBlocked(
                logger,
                message.OrganizationId,
                message.RepositoryId,
                message.OwnerQualifiedRepoName
            );
            await deliveries.MarkProcessedAsync(message.GitHubDeliveryId, cancellationToken);

            return message;
        }

        var reviewRequest = await reviewRequests.RequestAsync(
            CreateReviewRequest(message),
            cancellationToken
        );

        try
        {
            await telemetryLinker.LinkAsync(reviewRequest.PullRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            // NOTE: PR review workflow is authoritative; telemetry association is
            // best-effort and can be reconciled by later webhook deliveries.
            LogTelemetryLinkingFailed(
                logger,
                reviewRequest.PullRequest.OrganizationId,
                reviewRequest.PullRequest.OwnerQualifiedRepoName,
                reviewRequest.PullRequest.Branch,
                ex
            );
        }

        await deliveries.MarkProcessedAsync(message.GitHubDeliveryId, cancellationToken);
        LogPullRequestWorkflowProcessed(
            logger,
            message.OrganizationId,
            message.OwnerQualifiedRepoName,
            message.PullRequestNumber,
            message.GitHubAction,
            message.IsDraft,
            message.State
        );

        return message;
    }

    /// <summary>
    /// Re-checks that the repository mapping from ingress is still actionable.
    /// </summary>
    /// <remarks>
    /// Webhook ingress already resolves enabled repositories before publishing
    /// this message, but the queue creates a time gap. An admin can disable or
    /// reassign a repository before the workflow handler runs. This second gate
    /// keeps stale messages from creating PR rows or review work after the
    /// mapping has changed.
    /// </remarks>
    private async Task<bool> IsRepositoryStillEnabledAsync(
        GitHubPullRequestWebhookReceived message,
        CancellationToken cancellationToken
    )
    {
        var repository = await codeRepositories.FindActiveAsync(
            "github",
            message.OwnerQualifiedRepoName,
            cancellationToken
        );

        if (
            repository is null
            || repository.Id != message.RepositoryId
            || repository.OrganizationId != message.OrganizationId
        )
        {
            return false;
        }

        // NOTE: An organization-scoped repository mapping intentionally accepts
        // queued messages with any team value. Repository management can move a
        // repo from team scope to org scope while a webhook is waiting in the
        // queue, and the org-scoped mapping is the broader active authority.
        return repository.TeamId is null || repository.TeamId == message.TeamId;
    }

    /// <summary>
    /// Creates the idempotency row used to claim this GitHub delivery.
    /// </summary>
    /// <remarks>
    /// The delivery claim happens before PR or review writes. Only the caller
    /// that receives <see cref="WebhookDeliveryClaimResult.Claimed"/> should
    /// perform workflow side effects for the GitHub delivery id.
    /// </remarks>
    private static GitHubWebhookDelivery CreateDelivery(GitHubPullRequestWebhookReceived message) =>
        new() { DeliveryId = message.GitHubDeliveryId, ClaimedAtUtc = DateTimeOffset.UtcNow };

    /// <summary>
    /// Normalizes the provider-specific webhook message into the shared request input.
    /// </summary>
    /// <remarks>
    /// The delivery id is used both as the immediate comment signal id and the
    /// execution request's GitHub delivery id. Future manual requests should
    /// provide their own stable signal id and a non-webhook run correlation id,
    /// but they should not bypass the service's budget or active-lock gates.
    /// </remarks>
    private static CodeReviewRequest CreateReviewRequest(
        GitHubPullRequestWebhookReceived message
    ) =>
        new(
            OrganizationId: message.OrganizationId,
            TeamId: message.TeamId,
            RepositoryId: message.RepositoryId,
            OwnerQualifiedRepoName: message.OwnerQualifiedRepoName,
            PullRequestNumber: message.PullRequestNumber,
            PullRequestNodeId: message.PullRequestNodeId,
            Title: message.Title,
            HeadRef: message.HeadRef,
            BaseRef: message.BaseRef,
            HeadSha: message.HeadSha,
            AuthorLogin: message.AuthorLogin,
            HtmlUrl: message.HtmlUrl,
            IsDraft: message.IsDraft,
            State: message.State,
            TriggerAction: message.GitHubAction,
            RequestOrigin: CodeReviewRequestOrigin.RepositoryWebhook,
            SignalId: message.GitHubDeliveryId,
            RunRequestGitHubDeliveryId: message.GitHubDeliveryId,
            TraceContext: message.TraceContext
        );

    /// <summary>
    /// Starts the consumer activity that links queued work back to webhook ingress.
    /// </summary>
    /// <remarks>
    /// Webhook ingress captured the trace context before publishing this
    /// message. The handler resumes that context and tags the core GitHub,
    /// tenant, and PR identifiers used to diagnose downstream workflow work.
    /// </remarks>
    private static Activity? StartActivity(GitHubPullRequestWebhookReceived message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);

        return ZeeqTelemetry.Tracer.StartActivity(
            CreateActivityName(message.GitHubEvent, message.GitHubAction),
            ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("github.delivery_id", message.GitHubDeliveryId),
                new("github.event", message.GitHubEvent),
                new("github.action", message.GitHubAction),
                new("github.repo", message.OwnerQualifiedRepoName),
                new("organization.id", message.OrganizationId),
                new("pull_request.number", message.PullRequestNumber),
                new("github.installation_id", message.GitHubInstallationId),
            ]
        );
    }

    private static string CreateActivityName(string eventName, string action) =>
        string.IsNullOrWhiteSpace(action)
            ? $"github.webhook.{eventName}.workflow"
            : $"github.webhook.{eventName}.{action}.workflow";

    /// <summary>
    /// Logs a delivery replay that has already been claimed or processed.
    /// </summary>
    /// <remarks>
    /// Duplicate deliveries are expected from GitHub retries and queue replay.
    /// They are acknowledged without creating more PR, review, or comment work.
    /// </remarks>
    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Information,
        Message = "Skipping duplicate GitHub pull-request delivery. DeliveryId={DeliveryId}, Claim={ClaimResult}"
    )]
    private static partial void LogDuplicateDelivery(
        ILogger logger,
        string deliveryId,
        string claimResult
    );

    /// <summary>
    /// Logs a stale repository mapping message that was dropped before PR writes.
    /// </summary>
    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Information,
        Message = "Dropped GitHub pull-request workflow because repository mapping is no longer enabled. OrganizationId={OrganizationId}, RepositoryId={RepositoryId}, Repo={OwnerQualifiedRepoName}"
    )]
    private static partial void LogRepositoryGateBlocked(
        ILogger logger,
        string organizationId,
        string repositoryId,
        string ownerQualifiedRepoName
    );

    [LoggerMessage(
        EventId = 3205,
        Level = LogLevel.Warning,
        Message = "Best-effort telemetry PR linking failed. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, Branch={Branch}"
    )]
    private static partial void LogTelemetryLinkingFailed(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        string branch,
        Exception exception
    );

    [LoggerMessage(
        EventId = 3203,
        Level = LogLevel.Information,
        Message = "Received GitHub pull-request workflow. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, Action={Action}, IsDraft={IsDraft}, State={State}"
    )]
    private static partial void LogPullRequestWorkflowReceived(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        long pullRequestNumber,
        string action,
        bool isDraft,
        string? state
    );

    [LoggerMessage(
        EventId = 3204,
        Level = LogLevel.Information,
        Message = "Processed GitHub pull-request workflow. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, Action={Action}, IsDraft={IsDraft}, State={State}"
    )]
    private static partial void LogPullRequestWorkflowProcessed(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        long pullRequestNumber,
        string action,
        bool isDraft,
        string? state
    );
}
