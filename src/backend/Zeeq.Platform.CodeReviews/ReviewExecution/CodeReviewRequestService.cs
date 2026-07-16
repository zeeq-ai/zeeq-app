using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Creates code review requests from an already-authorized pull request workflow.
/// </summary>
/// <remarks>
/// This service owns the common durable request path shared by GitHub webhooks
/// and the future manual review endpoint: upsert the partitioned PR record,
/// apply review gates, enforce remaining budget, acquire the active-review
/// guard, create the review row, publish the immediate GitHub comment signal,
/// and finally publish the review-run message.
/// </remarks>
public sealed partial class CodeReviewRequestService(
    IPullRequestLookupStore lookups,
    IPullRequestRecordStore pullRequests,
    ICodeReviewRecordStore codeReviews,
    IActiveCodeReviewLockStore activeLocks,
    IZeeqMessagePublisher publisher,
    IOptions<AppSettings> appSettingsOptions,
    ICodeRepositoryStore repositories,
    ICheckRunService checkRunService,
    ILogger<CodeReviewRequestService> logger
)
{
    internal static readonly TimeSpan ActiveLockTtl = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Creates or gates a review request for one pull request.
    /// </summary>
    /// <remarks>
    /// Callers are responsible for ingress-specific work such as delivery
    /// idempotency, repository authorization, and final acknowledgement state.
    /// The returned outcome tells the caller whether a review was queued or the
    /// request was acknowledged through an immediate status comment only.
    /// </remarks>
    public async Task<CodeReviewRequestResult> RequestAsync(
        CodeReviewRequest request,
        CancellationToken cancellationToken
    )
    {
        LogReviewRequestStarted(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            request.PullRequestNumber,
            request.TriggerAction,
            request.IsDraft,
            request.State,
            request.BypassDraftGate,
            request.BypassTriggerActionGate
        );

        var pullRequest = await UpsertPullRequestAsync(request, cancellationToken);

        LogPullRequestUpserted(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            pullRequest.PullRequestNumber,
            pullRequest.Id,
            pullRequest.IsDraft,
            pullRequest.State
        );

        if (ShouldSkipReview(request, pullRequest, out var gateReason))
        {
            var commandKind = CommandKindForGate(gateReason);

            AddReviewGateEvent(request, pullRequest, gateReason);

            LogReviewRequestGated(
                logger,
                request.OrganizationId,
                request.OwnerQualifiedRepoName,
                pullRequest.PullRequestNumber,
                gateReason,
                request.TriggerAction,
                pullRequest.IsDraft,
                pullRequest.State,
                commandKind
            );

            if (ShouldPublishImmediateComment(commandKind))
            {
                await PublishImmediateCommentAsync(
                    request,
                    pullRequest,
                    commandKind,
                    codeReview: null,
                    cancellationToken
                );
                LogImmediateCommentSignalPublished(
                    logger,
                    request.OrganizationId,
                    request.OwnerQualifiedRepoName,
                    pullRequest.PullRequestNumber,
                    commandKind,
                    request.SignalId,
                    null
                );
            }

            return new(CodeReviewRequestOutcome.Gated, commandKind, pullRequest, CodeReview: null);
        }

        var latestReview = await codeReviews.FindNewestForPullRequestAsync(
            request.OrganizationId,
            pullRequest.Id,
            cancellationToken
        );
        LogLatestReviewLoaded(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            pullRequest.PullRequestNumber,
            latestReview?.Id,
            latestReview?.Status,
            latestReview?.RemainingReviewBudget
        );

        if (latestReview is { RemainingReviewBudget: <= 0 })
        {
            AddReviewGateEvent(request, pullRequest, "review_budget_exhausted");
            LogReviewRequestGated(
                logger,
                request.OrganizationId,
                request.OwnerQualifiedRepoName,
                pullRequest.PullRequestNumber,
                "review_budget_exhausted",
                request.TriggerAction,
                pullRequest.IsDraft,
                pullRequest.State,
                "allowance_exhausted"
            );

            await PublishImmediateCommentAsync(
                request,
                pullRequest,
                "allowance_exhausted",
                latestReview,
                cancellationToken
            );
            LogImmediateCommentSignalPublished(
                logger,
                request.OrganizationId,
                request.OwnerQualifiedRepoName,
                pullRequest.PullRequestNumber,
                "allowance_exhausted",
                request.SignalId,
                latestReview.Id
            );

            return new(
                CodeReviewRequestOutcome.BudgetExhausted,
                "allowance_exhausted",
                pullRequest,
                latestReview
            );
        }

        var reviewGroupId =
            latestReview?.ReviewGroupId ?? $"crg_{Guid.CreateVersion7():N}";
        var review = CreateCodeReview(
            request,
            pullRequest,
            latestReview?.RemainingReviewBudget ?? GetDefaultReviewBudget(),
            reviewGroupId
        );
        var activeLock = CreateActiveLock(request, pullRequest, review);
        LogReviewRecordCreatedInMemory(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            pullRequest.PullRequestNumber,
            review.Id,
            review.RemainingReviewBudget
        );

        if (!await activeLocks.TryAcquireAsync(activeLock, cancellationToken))
        {
            AddReviewGateEvent(request, pullRequest, "active_review");
            LogReviewRequestGated(
                logger,
                request.OrganizationId,
                request.OwnerQualifiedRepoName,
                pullRequest.PullRequestNumber,
                "active_review",
                request.TriggerAction,
                pullRequest.IsDraft,
                pullRequest.State,
                "already_running"
            );

            await PublishImmediateCommentAsync(
                request,
                pullRequest,
                "already_running",
                codeReview: null,
                cancellationToken
            );
            LogImmediateCommentSignalPublished(
                logger,
                request.OrganizationId,
                request.OwnerQualifiedRepoName,
                pullRequest.PullRequestNumber,
                "already_running",
                request.SignalId,
                null
            );

            return new(
                CodeReviewRequestOutcome.ActiveReviewAlreadyRunning,
                "already_running",
                pullRequest,
                CodeReview: null
            );
        }

        try
        {
            review = await codeReviews.AddAsync(review, cancellationToken);
        }
        catch
        {
            await activeLocks.ReleaseIfOwnedByReviewAsync(
                request.OrganizationId,
                pullRequest.Id,
                review.Id,
                review.CreatedAtUtc,
                cancellationToken
            );
            throw;
        }
        LogReviewRecordPersisted(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            pullRequest.PullRequestNumber,
            review.Id
        );

        await PublishImmediateCommentAsync(
            request,
            pullRequest,
            "queued",
            review,
            cancellationToken
        );
        LogImmediateCommentSignalPublished(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            pullRequest.PullRequestNumber,
            "queued",
            request.SignalId,
            review.Id
        );
        await PublishRunRequestedAsync(request, pullRequest, review, cancellationToken);
        LogRunRequestedSignalPublished(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            pullRequest.PullRequestNumber,
            review.Id,
            request.RunRequestGitHubDeliveryId
        );

        LogReviewQueued(
            logger,
            request.OrganizationId,
            request.OwnerQualifiedRepoName,
            request.PullRequestNumber,
            review.Id
        );

        if (!pullRequest.IsDraft)
        {
            // NOTE: This repository lookup adds an extra DB round-trip on every non-draft
            // review request. Callers such as GitHubPullRequestWebhookReceivedHandler already
            // load the repository for gating; if the check-run config were threaded through
            // the CodeReviewRequest or loaded once by the caller, this query could be avoided.
            var repository = await repositories.FindActiveForOrganizationAsync(
                request.OrganizationId,
                request.RepositoryId,
                cancellationToken
            );
            if (
                repository?.ReviewConfiguration.CheckRun.IsEnabled == true
            )
            {
                await checkRunService.MarkPendingAsync(pullRequest, CancellationToken.None);
            }
        }

        return new(CodeReviewRequestOutcome.Queued, "queued", pullRequest, review);
    }

    /// <summary>
    /// Writes the durable PR stream row and its lookup pointer.
    /// </summary>
    private async Task<PullRequestRecord> UpsertPullRequestAsync(
        CodeReviewRequest request,
        CancellationToken cancellationToken
    )
    {
        var pullRequestNumber = ToPullRequestNumber(request.PullRequestNumber);
        var now = DateTimeOffset.UtcNow;
        var lookup = await lookups.FindAsync(
            request.OrganizationId,
            request.RepositoryId,
            pullRequestNumber,
            cancellationToken
        );

        var recordId =
            request.PullRequestRecordId
            ?? lookup?.PullRequestRecordId
            ?? $"pr_{Guid.CreateVersion7():N}";
        var createdAtUtc =
            request.PullRequestCreatedAtUtc ?? lookup?.PullRequestCreatedAtUtc ?? now;
        var pullRequest = new PullRequestRecord
        {
            Id = recordId,
            OrganizationId = request.OrganizationId,
            TeamId = request.TeamId,
            RepositoryId = request.RepositoryId,
            OwnerQualifiedRepoName = request.OwnerQualifiedRepoName,
            PullRequestNumber = pullRequestNumber,
            GitHubNodeId = request.PullRequestNodeId ?? string.Empty,
            Title = request.Title ?? $"Pull request #{pullRequestNumber}",
            Branch = request.HeadRef ?? string.Empty,
            BaseBranch = request.BaseRef ?? string.Empty,
            HeadSha = request.HeadSha ?? string.Empty,
            AuthorLogin = request.AuthorLogin ?? string.Empty,
            HtmlUrl = request.HtmlUrl ?? string.Empty,
            IsDraft = request.IsDraft,
            State = ParsePullRequestState(request.State),
            ClaimStatus = request.PullRequestClaimStatus ?? PullRequestClaimStatus.Unclaimed,
            ClaimedByUserId = request.PullRequestClaimedByUserId,
            FeatureId = request.PullRequestFeatureId,
            TagsJson = request.PullRequestTagsJson ?? "[]",
            LabelsJson = request.PullRequestLabelsJson ?? "[]",
            CreatedFromWebhookAtUtc = request.PullRequestCreatedFromWebhookAtUtc ?? createdAtUtc,
            LastWebhookAtUtc = request.PullRequestLastWebhookAtUtc ?? now,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = now,
        };

        pullRequest = await pullRequests.UpsertAsync(pullRequest, cancellationToken);

        await lookups.UpsertAsync(
            new PullRequestLookup
            {
                OrganizationId = request.OrganizationId,
                TeamId = request.TeamId,
                RepositoryId = request.RepositoryId,
                OwnerQualifiedRepoName = request.OwnerQualifiedRepoName,
                PullRequestNumber = pullRequestNumber,
                PullRequestRecordId = pullRequest.Id,
                PullRequestCreatedAtUtc = pullRequest.CreatedAtUtc,
                UpdatedAtUtc = now,
            },
            cancellationToken
        );

        return pullRequest;
    }

    private static bool ShouldSkipReview(
        CodeReviewRequest request,
        PullRequestRecord pullRequest,
        out string reason
    )
    {
        if (pullRequest.IsDraft && !request.BypassDraftGate)
        {
            reason = "draft";
            return true;
        }

        if (pullRequest.State != PullRequestState.Open && !request.BypassStateGate)
        {
            reason = "closed";
            return true;
        }

        if (!request.BypassTriggerActionGate && !IsReviewTriggerAction(request.TriggerAction))
        {
            reason = "ignored_action";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string CommandKindForGate(string gateReason) =>
        gateReason switch
        {
            "draft" => "draft_prompt",
            "closed" => "closed",
            _ => "ignored",
        };

    private static bool ShouldPublishImmediateComment(string commandKind) =>
        commandKind is not "closed";

    private static bool IsReviewTriggerAction(string action) =>
        action is "opened" or "reopened" or "ready_for_review" or "synchronize";

    private int GetDefaultReviewBudget() =>
        Math.Max(0, appSettingsOptions.Value.CodeReview.DefaultReviewBudget);

    private static CodeReviewRecord CreateCodeReview(
        CodeReviewRequest request,
        PullRequestRecord pullRequest,
        int remainingReviewBudget,
        string reviewGroupId
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = $"cr_{Guid.CreateVersion7():N}",
            OrganizationId = request.OrganizationId,
            TeamId = request.TeamId,
            PullRequestRecordId = pullRequest.Id,
            RepositoryId = request.RepositoryId,
            OwnerQualifiedRepoName = request.OwnerQualifiedRepoName,
            PullRequestNumber = pullRequest.PullRequestNumber,
            Branch = pullRequest.Branch,
            Title = pullRequest.Title,
            AuthorLogin = pullRequest.AuthorLogin,
            Status = CodeReviewStatus.Pending,
            RequestOrigin = request.RequestOrigin,
            ReviewGroupId = reviewGroupId,
            RemainingReviewBudget = remainingReviewBudget,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static ActiveCodeReviewLock CreateActiveLock(
        CodeReviewRequest request,
        PullRequestRecord pullRequest,
        CodeReviewRecord review
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            OrganizationId = request.OrganizationId,
            TeamId = request.TeamId,
            RepositoryId = request.RepositoryId,
            PullRequestRecordId = pullRequest.Id,
            PullRequestCreatedAtUtc = pullRequest.CreatedAtUtc,
            CodeReviewRecordId = review.Id,
            CodeReviewCreatedAtUtc = review.CreatedAtUtc,
            Status = CodeReviewStatus.Pending,
            AcquiredAtUtc = now,
            ExpiresAtUtc = now.Add(ActiveLockTtl),
            UpdatedAtUtc = now,
        };
    }

    private async Task PublishImmediateCommentAsync(
        CodeReviewRequest request,
        PullRequestRecord pullRequest,
        string commandKind,
        CodeReviewRecord? codeReview,
        CancellationToken cancellationToken
    )
    {
        await publisher.PublishAsync(
            new GitHubCommentWriteRequested
            {
                OrganizationId = request.OrganizationId,
                TeamId = request.TeamId,
                RepositoryId = request.RepositoryId,
                OwnerQualifiedRepoName = request.OwnerQualifiedRepoName,
                PullRequestNumber = pullRequest.PullRequestNumber,
                Target = CreatePullRequestSummaryTarget(request, pullRequest),
                Kind = commandKind,
                Clear = ClearMarkersFor(commandKind),
                CodeReviewRecordId = codeReview?.Id,
                CodeReviewCreatedAtUtc = codeReview?.CreatedAtUtc,
                SignalId = request.SignalId,
                TraceContext = request.TraceContext,
            },
            cancellationToken
        );
    }

    private static GitHubCommentTargetSelector CreatePullRequestSummaryTarget(
        CodeReviewRequest request,
        PullRequestRecord pullRequest
    ) =>
        new(
            OrganizationId: request.OrganizationId,
            RepositoryId: request.RepositoryId,
            PullRequestNumber: pullRequest.PullRequestNumber,
            Kind: GitHubCommentTargetKind.PullRequestSummary,
            ScopeKey: GitHubCommentMarkers.PullRequestSummaryScopeKey
        );

    private static IReadOnlyList<string> ClearMarkersFor(string commandKind) =>
        commandKind is "queued" or "draft_prompt" or "closed"
            ?
            [
                GitHubCommentMarkers.PullRequestStatus,
                GitHubCommentMarkers.PullRequestFindings,
                GitHubCommentMarkers.PullRequestEvidence,
            ]
            : [GitHubCommentMarkers.PullRequestStatus];

    private async Task PublishRunRequestedAsync(
        CodeReviewRequest request,
        PullRequestRecord pullRequest,
        CodeReviewRecord codeReview,
        CancellationToken cancellationToken
    ) =>
        await publisher.PublishAsync(
            new CodeReviewRunRequested
            {
                OrganizationId = request.OrganizationId,
                TeamId = request.TeamId,
                RepositoryId = request.RepositoryId,
                OwnerQualifiedRepoName = request.OwnerQualifiedRepoName,
                PullRequestNumber = pullRequest.PullRequestNumber,
                PullRequestRecordId = pullRequest.Id,
                PullRequestCreatedAtUtc = pullRequest.CreatedAtUtc,
                CodeReviewRecordId = codeReview.Id,
                CodeReviewCreatedAtUtc = codeReview.CreatedAtUtc,
                GitHubDeliveryId = request.RunRequestGitHubDeliveryId,
                TraceContext = request.TraceContext,
            },
            cancellationToken
        );

    private static void AddReviewGateEvent(
        CodeReviewRequest request,
        PullRequestRecord pullRequest,
        string gateReason
    ) =>
        ZeeqTelemetry.AddEvent(
            [
                ("github.delivery_id", request.SignalId),
                ("github.repo", request.OwnerQualifiedRepoName),
                ("pull_request.number", pullRequest.PullRequestNumber),
                ("gate.reason", gateReason),
            ],
            eventName: "pr.review_gated"
        );

    private static PullRequestState ParsePullRequestState(string? state) =>
        state?.Trim().ToLowerInvariant() switch
        {
            "closed" => PullRequestState.Closed,
            "merged" => PullRequestState.Merged,
            _ => PullRequestState.Open,
        };

    private static int ToPullRequestNumber(long pullRequestNumber)
    {
        if (pullRequestNumber is < 0 or > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"GitHub pull request number {pullRequestNumber} is outside the supported range."
            );
        }

        return (int)pullRequestNumber;
    }

    [LoggerMessage(
        EventId = 3210,
        Level = LogLevel.Information,
        Message = "Queued GitHub code review. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogReviewQueued(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        long pullRequestNumber,
        string codeReviewId
    );

    [LoggerMessage(
        EventId = 3211,
        Level = LogLevel.Information,
        Message = "Started code-review request. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, TriggerAction={TriggerAction}, IsDraft={IsDraft}, State={State}, BypassDraftGate={BypassDraftGate}, BypassTriggerActionGate={BypassTriggerActionGate}"
    )]
    private static partial void LogReviewRequestStarted(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        long pullRequestNumber,
        string triggerAction,
        bool isDraft,
        string? state,
        bool bypassDraftGate,
        bool bypassTriggerActionGate
    );

    [LoggerMessage(
        EventId = 3212,
        Level = LogLevel.Information,
        Message = "Upserted pull request for code-review request. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, PullRequestRecordId={PullRequestRecordId}, IsDraft={IsDraft}, PullRequestState={PullRequestState}"
    )]
    private static partial void LogPullRequestUpserted(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string pullRequestRecordId,
        bool isDraft,
        PullRequestState pullRequestState
    );

    [LoggerMessage(
        EventId = 3213,
        Level = LogLevel.Information,
        Message = "Gated code-review request. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, GateReason={GateReason}, TriggerAction={TriggerAction}, IsDraft={IsDraft}, PullRequestState={PullRequestState}, CommentKind={CommentKind}"
    )]
    private static partial void LogReviewRequestGated(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string gateReason,
        string triggerAction,
        bool isDraft,
        PullRequestState pullRequestState,
        string commentKind
    );

    [LoggerMessage(
        EventId = 3214,
        Level = LogLevel.Information,
        Message = "Published immediate GitHub comment signal for code-review request. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, Kind={Kind}, SignalId={SignalId}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogImmediateCommentSignalPublished(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string kind,
        string signalId,
        string? codeReviewId
    );

    [LoggerMessage(
        EventId = 3215,
        Level = LogLevel.Information,
        Message = "Loaded latest code review for pull request. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, LatestCodeReviewId={LatestCodeReviewId}, LatestStatus={LatestStatus}, RemainingReviewBudget={RemainingReviewBudget}"
    )]
    private static partial void LogLatestReviewLoaded(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string? latestCodeReviewId,
        CodeReviewStatus? latestStatus,
        int? remainingReviewBudget
    );

    [LoggerMessage(
        EventId = 3216,
        Level = LogLevel.Information,
        Message = "Created code-review record before persistence. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}, RemainingReviewBudget={RemainingReviewBudget}"
    )]
    private static partial void LogReviewRecordCreatedInMemory(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId,
        int remainingReviewBudget
    );

    [LoggerMessage(
        EventId = 3217,
        Level = LogLevel.Information,
        Message = "Persisted code-review record. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogReviewRecordPersisted(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId
    );

    [LoggerMessage(
        EventId = 3218,
        Level = LogLevel.Information,
        Message = "Published code-review run request signal. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}, GitHubDeliveryId={GitHubDeliveryId}"
    )]
    private static partial void LogRunRequestedSignalPublished(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId,
        string githubDeliveryId
    );
}

/// <summary>
/// Provider-neutral input for creating a code review request.
/// </summary>
public sealed record CodeReviewRequest(
    string OrganizationId,
    string? TeamId,
    string RepositoryId,
    string OwnerQualifiedRepoName,
    long PullRequestNumber,
    string? PullRequestNodeId,
    string? Title,
    string? HeadRef,
    string? BaseRef,
    string? HeadSha,
    string? AuthorLogin,
    string? HtmlUrl,
    bool IsDraft,
    string? State,
    string TriggerAction,
    CodeReviewRequestOrigin RequestOrigin,
    string SignalId,
    string RunRequestGitHubDeliveryId,
    ZeeqTraceContext TraceContext,
    bool BypassDraftGate = false,
    bool BypassTriggerActionGate = false,
    bool BypassStateGate = false,
    string? PullRequestRecordId = null,
    DateTimeOffset? PullRequestCreatedAtUtc = null,
    DateTimeOffset? PullRequestCreatedFromWebhookAtUtc = null,
    DateTimeOffset? PullRequestLastWebhookAtUtc = null,
    PullRequestClaimStatus? PullRequestClaimStatus = null,
    string? PullRequestClaimedByUserId = null,
    string? PullRequestFeatureId = null,
    string? PullRequestTagsJson = null,
    string? PullRequestLabelsJson = null
);

/// <summary>
/// Result of attempting to create a code review request.
/// </summary>
public sealed record CodeReviewRequestResult(
    CodeReviewRequestOutcome Outcome,
    string CommentKind,
    PullRequestRecord PullRequest,
    CodeReviewRecord? CodeReview
);

/// <summary>
/// Durable outcome category for a code review request attempt.
/// </summary>
public enum CodeReviewRequestOutcome
{
    /// <summary>
    /// A review record was created and execution work was published.
    /// </summary>
    Queued,

    /// <summary>
    /// The request was acknowledged but skipped by a lightweight PR gate.
    /// </summary>
    Gated,

    /// <summary>
    /// No review was created because the pull request already has active work.
    /// </summary>
    ActiveReviewAlreadyRunning,

    /// <summary>
    /// No review was created because the remaining review budget is exhausted.
    /// </summary>
    BudgetExhausted,
}
