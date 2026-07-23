using System.Diagnostics;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runs queued code-review execution work.
/// </summary>
/// <remarks>
/// This handler owns the durable orchestration around review execution: load the
/// partitioned review row, acquire an organization-scoped execution lease, keep
/// that lease renewed while model work runs, and only release the active PR lock
/// after the review reaches a terminal state. The expensive review engine stays
/// behind <see cref="ICodeReviewRunner"/>.
/// </remarks>
[ConfigureConsumer<CodeReviewRunRequested>(
    "code-review.run",
    // CodeReviewRunRequested is ITenantMessage, which already fans this
    // subscription out across every priority-tier bucket (20 by default: 8
    // priority + 8 default + 4 low) — noOfPerformers here multiplies by that
    // bucket count, it doesn't add to it. noOfPerformers: 2 was silently
    // producing 40 real performer threads for this handler alone.
    noOfPerformers: 1,
    bufferSize: 16,
    visibleTimeoutSeconds: 900,
    pollIntervalMilliseconds: 100
)]
public sealed partial class CodeReviewRunRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    ICodeReviewRecordStore codeReviews,
    IActiveCodeReviewLockStore activeLocks,
    ICodeReviewOrganizationSettingsStore organizationSettings,
    ICodeReviewExecutionLeaseStore executionLeases,
    IZeeqMessagePublisher publisher,
    ICodeReviewRunner runner,
    ICodeReviewRuntimeStatistics runtimeStatistics,
    IServiceScopeFactory serviceScopeFactory,
    ICheckRunService checkRunService,
    IPullRequestRecordStore pullRequestRecords,
    ICodeRepositoryStore repositories,
    IGitHubCommentClientFactory commentClientFactory,
    CodeReviewRequestLinkFactory linkFactory,
    ILogger<CodeReviewRunRequestedHandler> logger,
    TimeProvider? timeProvider = null
) : ZeeqMessageHandler<CodeReviewRunRequested>(deadLetterWriter)
{
    private static readonly TimeSpan CapacityDeferralDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CapacityDeferralMaxAge = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task<CodeReviewRunRequested> HandleMessageAsync(
        CodeReviewRunRequested message,
        CancellationToken cancellationToken
    )
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);

        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "code-review.run",
            System.Diagnostics.ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("github.delivery_id", message.GitHubDeliveryId),
                new("github.repo", message.OwnerQualifiedRepoName),
                new("organization.id", message.OrganizationId),
                new("code_review.id", message.CodeReviewRecordId),
                new("pull_request.number", message.PullRequestNumber),
            ]
        );

        LogRunMessageReceived(
            logger,
            message.OrganizationId,
            message.OwnerQualifiedRepoName,
            message.PullRequestNumber,
            message.CodeReviewRecordId,
            message.RepositoryId,
            message.PullRequestRecordId
        );

        var review =
            await codeReviews.FindAsync(
                message.CodeReviewRecordId,
                message.CodeReviewCreatedAtUtc,
                cancellationToken
            )
            ?? throw new InvalidOperationException(
                $"Code review record {message.CodeReviewRecordId} was not found."
            );

        if (IsTerminal(review.Status))
        {
            LogTerminalReviewSkipped(
                logger,
                message.OrganizationId,
                message.CodeReviewRecordId,
                review.Status
            );

            await ReleaseActiveLockIfOwnedAsync(message, cancellationToken);

            return message;
        }

        var settings = await organizationSettings.GetAsync(
            message.OrganizationId,
            cancellationToken
        );

        CodeReviewExecutionLeaseResult leaseResult;

        while (true)
        {
            var leaseRequest = CreateLeaseRequest(message, settings);

            LogExecutionLeaseAcquisitionStarting(
                logger,
                message.OrganizationId,
                message.CodeReviewRecordId,
                settings.MaxConcurrentReviews
            );

            try
            {
                leaseResult = await executionLeases.TryAcquireAsync(
                    leaseRequest,
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                LogExecutionLeaseAcquisitionFailed(
                    logger,
                    ex,
                    message.OrganizationId,
                    message.CodeReviewRecordId,
                    ex.GetType().Name,
                    ex.Message
                );

                throw;
            }

            if (leaseResult.Outcome != CodeReviewExecutionLeaseOutcome.NoSlotAvailable)
            {
                break;
            }

            var retry = await HandleCapacityUnavailableAsync(
                message,
                review,
                settings,
                activity,
                cancellationToken
            );

            if (!retry)
            {
                return message;
            }

            settings = await organizationSettings.GetAsync(
                message.OrganizationId,
                cancellationToken
            );
        }

        if (leaseResult.Outcome == CodeReviewExecutionLeaseOutcome.AlreadyLeasedForThisReview)
        {
            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("code_review.execution_lease_outcome", leaseResult.Outcome.ToString()),
                ],
                "code_review.execution_lease_already_held"
            );

            LogExecutionLeaseAlreadyHeld(
                logger,
                message.OrganizationId,
                message.CodeReviewRecordId
            );

            return message;
        }

        var lease =
            leaseResult.Lease
            ?? throw new InvalidOperationException(
                "Execution lease acquisition returned Acquired without a lease."
            );

        var reachedTerminalState = false;
        long? runStartedAt = null;
        var runtimeRecorded = false;

        try
        {
            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("code_review.execution_lease_id", lease.LeaseId),
                    ("code_review.max_concurrent_reviews", settings.MaxConcurrentReviews),
                ],
                "code_review.execution_lease_acquired"
            );

            LogExecutionLeaseAcquired(
                logger,
                message.OrganizationId,
                message.CodeReviewRecordId,
                lease.LeaseId,
                lease.SlotIndex,
                settings.MaxConcurrentReviews
            );

            review.Status = CodeReviewStatus.Running;
            review.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await codeReviews.UpdateAsync(review, cancellationToken);

            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                ],
                "code_review.run_started"
            );

            LogRunnerStarting(
                logger,
                message.OrganizationId,
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber,
                message.CodeReviewRecordId
            );

            runStartedAt = _timeProvider.GetTimestamp();

            // This is the runner here where we hold on to the lease while running
            // the review.
            var result = await RunWithLeaseRenewalAsync(
                message,
                review,
                lease,
                settings.ExecutionLeaseDuration,
                cancellationToken
            );

            LogRunnerReturned(
                logger,
                message.OrganizationId,
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber,
                message.CodeReviewRecordId,
                result.CriticalFindings,
                result.MajorFindings,
                result.MinorFindings,
                result.SuggestionFindings,
                result.CommentFindings
            );

            var runtime = _timeProvider.GetElapsedTime(runStartedAt.Value);

            await RecordReviewRuntimeAsync(message, runtime, activity, cancellationToken);

            runtimeRecorded = true;
            var runtimeSnapshot = runtimeStatistics.GetSnapshot();

            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("code_review.runtime_ms", runtime.TotalMilliseconds),
                    ("code_review.runtime.p50_ms", runtimeSnapshot.Percentile50?.TotalMilliseconds),
                    ("code_review.runtime.p95_ms", runtimeSnapshot.Percentile95?.TotalMilliseconds),
                    ("code_review.findings.critical", result.CriticalFindings),
                    ("code_review.findings.major", result.MajorFindings),
                    ("code_review.findings.minor", result.MinorFindings),
                    ("code_review.findings.suggestion", result.SuggestionFindings),
                    ("code_review.findings.comment", result.CommentFindings),
                ],
                "code_review.runner_completed"
            );

            review.Status = CodeReviewStatus.Completed;
            review.SourceTelemetryPayload = NormalizeTelemetryPayload(
                result.SourceTelemetryPayload
            );
            review.FindingsStorageUri = result.FindingsStorageUri;
            review.CriticalFindings = result.CriticalFindings;
            review.MajorFindings = result.MajorFindings;
            review.MinorFindings = result.MinorFindings;
            review.SuggestionFindings = result.SuggestionFindings;
            review.CommentFindings = result.CommentFindings;
            review.FailureMessage = null;
            review.RemainingReviewBudget = Math.Max(0, review.RemainingReviewBudget - 1);
            review.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await codeReviews.UpdateAsync(review, cancellationToken);

            reachedTerminalState = true;

            await publisher.PublishAsync(
                CreateCommentWriteSignal(message, GitHubCommentKinds.ReviewCompleted),
                cancellationToken
            );
            LogCommentSignalPublished(
                logger,
                message.OrganizationId,
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber,
                message.CodeReviewRecordId,
                GitHubCommentKinds.ReviewCompleted
            );

            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("github.comment.kind", GitHubCommentKinds.ReviewCompleted),
                ],
                "code_review.comment_signal_published"
            );

            await TryResolveCheckRunAsync(message, review, activity);
        }
        catch (Exception ex)
        {
            if (runStartedAt.HasValue && !runtimeRecorded)
            {
                await RecordReviewRuntimeAsync(
                    message,
                    _timeProvider.GetElapsedTime(runStartedAt.Value),
                    activity,
                    CancellationToken.None
                );
            }

            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("exception.type", ex.GetType().Name),
                    ("exception.message", ex.Message),
                ],
                "code_review.run_failed"
            );

            review.Status = CodeReviewStatus.Errored;
            // The runner sets review.SourceTelemetryPayload with partial telemetry captured before
            // the failure (see CodeReviewRunner's catch); normalize whatever is present.
            review.SourceTelemetryPayload = NormalizeTelemetryPayload(
                review.SourceTelemetryPayload
            );
            review.FailureMessage = ex.Message;
            review.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await codeReviews.UpdateAsync(review, cancellationToken);
            reachedTerminalState = true;

            await publisher.PublishAsync(
                CreateCommentWriteSignal(message, GitHubCommentKinds.ReviewFailed),
                CancellationToken.None
            );
            LogCommentSignalPublished(
                logger,
                message.OrganizationId,
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber,
                message.CodeReviewRecordId,
                GitHubCommentKinds.ReviewFailed
            );

            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("github.comment.kind", GitHubCommentKinds.ReviewFailed),
                ],
                "code_review.comment_signal_published"
            );

            await TryResolveCheckRunAsync(message, review, activity);

            LogReviewFailed(
                logger,
                ex,
                message.OrganizationId,
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber,
                message.CodeReviewRecordId,
                ex.GetType().Name,
                ex.Message
            );

            throw;
        }
        finally
        {
            await executionLeases.ReleaseAsync(lease.LeaseId, CancellationToken.None);
            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("code_review.execution_lease_id", lease.LeaseId),
                    ("code_review.reached_terminal_state", reachedTerminalState),
                ],
                "code_review.execution_lease_released"
            );

            if (reachedTerminalState)
            {
                await ReleaseActiveLockIfOwnedAsync(message, CancellationToken.None);
            }
        }

        LogReviewCompleted(
            logger,
            message.OrganizationId,
            message.OwnerQualifiedRepoName,
            message.PullRequestNumber,
            message.CodeReviewRecordId
        );

        return message;
    }

    private async Task TryResolveCheckRunAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        System.Diagnostics.Activity? activity
    )
    {
        if (string.IsNullOrWhiteSpace(message.PullRequestRecordId))
        {
            return;
        }

        try
        {
            var pr = await pullRequestRecords.FindAsync(
                message.PullRequestRecordId,
                message.PullRequestCreatedAtUtc,
                CancellationToken.None
            );
            if (pr is null)
            {
                LogCheckRunResolvePrMissing(
                    logger,
                    message.OrganizationId,
                    message.OwnerQualifiedRepoName,
                    message.PullRequestNumber,
                    message.PullRequestRecordId,
                    message.CodeReviewRecordId
                );
                await TryPostCheckRunResolutionFailureCommentAsync(message, review);
                return;
            }

            CodeRepositoryReviewCheckRunConfiguration config;
            if (message.RepositoryId is not null)
            {
                var repository = await repositories.FindActiveForOrganizationAsync(
                    message.OrganizationId,
                    message.RepositoryId,
                    CancellationToken.None
                );
                config =
                    repository?.ReviewConfiguration.CheckRun
                    ?? CodeRepositoryReviewCheckRunConfiguration.Empty;
            }
            else
            {
                config = CodeRepositoryReviewCheckRunConfiguration.Empty;
            }

            await checkRunService.ResolveFromReviewAsync(
                review,
                pr,
                config,
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            activity?.AddEvent(
                [("exception.type", ex.GetType().Name), ("exception.message", ex.Message)],
                "code_review.check_run_resolve_failed"
            );
        }
    }

    private async Task TryPostCheckRunResolutionFailureCommentAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review
    )
    {
        try
        {
            var commentClient = await commentClientFactory.CreateForOrganizationAsync(
                message.OrganizationId,
                CancellationToken.None
            );
            var reviewUrl = linkFactory.BuildSingleReviewLink(review, CodeReviewSingleViewMode.Pr);
            var body = $"""
                > [!WARNING]
                > Zeeq could not resolve the check run for this pull request. If a merge block is active, [view the review in Zeeq]({reviewUrl}) or bypass it by commenting `/bb bypass check`.
                """;
            await commentClient.CreateIssueCommentAsync(
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber,
                body,
                CancellationToken.None
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort comment; failure here is not actionable
        }
    }

    private async Task<bool> HandleCapacityUnavailableAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        CodeReviewOrganizationSettings settings,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken
    )
    {
        activity?.AddEvent(
            new(
                "code_review.capacity_deferred",
                tags:
                [
                    new("organization.id", message.OrganizationId),
                    new("code_review.id", message.CodeReviewRecordId),
                    new("code_review.max_concurrent_reviews", settings.MaxConcurrentReviews),
                ]
            )
        );

        if (_timeProvider.GetUtcNow() - review.CreatedAtUtc >= CapacityDeferralMaxAge)
        {
            review.Status = CodeReviewStatus.Errored;
            review.FailureMessage =
                "Code review capacity remained unavailable before the retry window expired.";
            review.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await codeReviews.UpdateAsync(review, cancellationToken);

            await publisher.PublishAsync(
                CreateCommentWriteSignal(message, GitHubCommentKinds.ReviewFailed),
                cancellationToken
            );
            LogCommentSignalPublished(
                logger,
                message.OrganizationId,
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber,
                message.CodeReviewRecordId,
                GitHubCommentKinds.ReviewFailed
            );

            await ReleaseActiveLockIfOwnedAsync(message, cancellationToken);

            LogCapacityDeferralExhausted(
                logger,
                message.OrganizationId,
                message.CodeReviewRecordId
            );

            return false;
        }

        LogCapacityDeferred(
            logger,
            message.OrganizationId,
            message.CodeReviewRecordId,
            settings.MaxConcurrentReviews,
            CapacityDeferralDelay
        );
        await Task.Delay(CapacityDeferralDelay, _timeProvider, cancellationToken);

        return true;
    }

    private static string NormalizeTelemetryPayload(string telemetryPayload) =>
        string.IsNullOrWhiteSpace(telemetryPayload)
            ? CodeReviewRecord.EmptySourceTelemetryPayload
            : telemetryPayload;

    private async Task<CodeReviewRunResult> RunWithLeaseRenewalAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        CodeReviewExecutionLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workTask = runner.RunAsync(message, review, workCts.Token);
        Task[] renewalTasks =
        [
            RunExecutionLeaseRenewalAsync(lease.LeaseId, leaseDuration, workCts),
            RunActiveLockRenewalAsync(message, CodeReviewRequestService.ActiveLockTtl, workCts),
        ];

        try
        {
            return await AwaitWorkWithRenewalsAsync(workTask, renewalTasks, workCts);
        }
        finally
        {
            await workCts.CancelAsync();
            await IgnoreRenewalShutdownAsync(renewalTasks);
        }
    }

    private async ValueTask RecordReviewRuntimeAsync(
        CodeReviewRunRequested message,
        TimeSpan runtime,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await runtimeStatistics.RecordAsync(runtime, cancellationToken);
            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("code_review.runtime_ms", runtime.TotalMilliseconds),
                ],
                "code_review.runtime_recorded"
            );
        }
        catch (Exception ex)
        {
            activity?.AddEvent(
                [
                    ("organization.id", message.OrganizationId),
                    ("code_review.id", message.CodeReviewRecordId),
                    ("exception.type", ex.GetType().Name),
                    ("exception.message", ex.Message),
                ],
                "code_review.runtime_record_failed"
            );
        }
    }

    private static async Task<CodeReviewRunResult> AwaitWorkWithRenewalsAsync(
        Task<CodeReviewRunResult> workTask,
        IReadOnlyList<Task> renewalTasks,
        CancellationTokenSource workCts
    )
    {
        var firstRenewalTask = Task.WhenAny(renewalTasks);
        var completedTask = await Task.WhenAny(workTask, firstRenewalTask);
        if (completedTask == workTask)
        {
            try
            {
                return await workTask;
            }
            catch (OperationCanceledException) when (workCts.IsCancellationRequested)
            {
                var renewalTask = await firstRenewalTask;
                await renewalTask;
                throw;
            }
        }

        try
        {
            var renewalTask = await firstRenewalTask;
            await renewalTask;
        }
        finally
        {
            await workCts.CancelAsync();
            await IgnoreCanceledWorkAsync(workTask, workCts.Token);
        }

        throw new InvalidOperationException("Code review renewal ended before review work.");
    }

    private async Task RunExecutionLeaseRenewalAsync(
        string leaseId,
        TimeSpan leaseDuration,
        CancellationTokenSource workCts
    )
    {
        var interval = ResolveRenewalInterval(leaseDuration);

        while (true)
        {
            await Task.Delay(interval, workCts.Token);

            var renewed = await executionLeases.RenewAsync(leaseId, leaseDuration, workCts.Token);

            if (renewed)
            {
                continue;
            }

            await workCts.CancelAsync();

            throw new CodeReviewExecutionLeaseLostException(leaseId);
        }
    }

    private async Task RunActiveLockRenewalAsync(
        CodeReviewRunRequested message,
        TimeSpan activeLockTtl,
        CancellationTokenSource workCts
    )
    {
        var interval = ResolveRenewalInterval(activeLockTtl);

        while (true)
        {
            await Task.Delay(interval, workCts.Token);

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedActiveLocks =
                scope.ServiceProvider.GetRequiredService<IActiveCodeReviewLockStore>();
            var renewed = await scopedActiveLocks.RefreshAsync(
                message.OrganizationId,
                message.PullRequestRecordId,
                activeLockTtl,
                workCts.Token
            );

            if (renewed)
            {
                continue;
            }

            await workCts.CancelAsync();

            throw new CodeReviewActiveLockLostException(
                message.OrganizationId,
                message.PullRequestRecordId,
                message.CodeReviewRecordId
            );
        }
    }

    private static CodeReviewExecutionLeaseRequest CreateLeaseRequest(
        CodeReviewRunRequested message,
        CodeReviewOrganizationSettings settings
    ) =>
        new(
            OrganizationId: message.OrganizationId,
            TeamId: message.TeamId,
            RepositoryId: message.RepositoryId,
            PullRequestRecordId: message.PullRequestRecordId,
            PullRequestCreatedAtUtc: message.PullRequestCreatedAtUtc,
            CodeReviewRecordId: message.CodeReviewRecordId,
            CodeReviewCreatedAtUtc: message.CodeReviewCreatedAtUtc,
            MaxConcurrentReviews: settings.MaxConcurrentReviews,
            LeaseDuration: settings.ExecutionLeaseDuration,
            WorkerId: CreateWorkerId()
        );

    private static GitHubCommentWriteRequested CreateCommentWriteSignal(
        CodeReviewRunRequested message,
        string kind
    ) =>
        new()
        {
            OrganizationId = message.OrganizationId,
            TeamId = message.TeamId,
            RepositoryId = message.RepositoryId,
            OwnerQualifiedRepoName = message.OwnerQualifiedRepoName,
            PullRequestNumber = message.PullRequestNumber,
            Target = CreatePullRequestSummaryTarget(message),
            Kind = kind,
            Clear =
            [
                GitHubCommentMarkers.PullRequestStatus,
                GitHubCommentMarkers.PullRequestFindings,
                GitHubCommentMarkers.PullRequestEvidence,
            ],
            CodeReviewRecordId = message.CodeReviewRecordId,
            CodeReviewCreatedAtUtc = message.CodeReviewCreatedAtUtc,
            SignalId = message.CodeReviewRecordId,
            TraceContext = message.TraceContext,
        };

    private static TimeSpan ResolveRenewalInterval(TimeSpan leaseDuration)
    {
        var interval = leaseDuration / 2;
        return interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(1);
    }

    private static bool IsTerminal(CodeReviewStatus status) =>
        status
            is CodeReviewStatus.Completed
                or CodeReviewStatus.Errored
                or CodeReviewStatus.Cancelled;

    private static string CreateWorkerId() =>
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.CreateVersion7():N}";

    private Task ReleaseActiveLockIfOwnedAsync(
        CodeReviewRunRequested message,
        CancellationToken cancellationToken
    ) =>
        activeLocks.ReleaseIfOwnedByReviewAsync(
            message.OrganizationId,
            message.PullRequestRecordId,
            message.CodeReviewRecordId,
            message.CodeReviewCreatedAtUtc,
            cancellationToken
        );

    private static async Task IgnoreRenewalShutdownAsync(IReadOnlyList<Task> renewalTasks)
    {
        foreach (var renewalTask in renewalTasks)
        {
            await IgnoreRenewalShutdownAsync(renewalTask);
        }
    }

    private static async Task IgnoreRenewalShutdownAsync(Task renewalTask)
    {
        try
        {
            await renewalTask;
        }
        catch (OperationCanceledException)
        {
            // Expected after review work completes and the linked token is cancelled.
        }
    }

    private static async Task IgnoreCanceledWorkAsync(
        Task workTask,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await workTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Builds the logical top-level PR comment target for completed review output.
    /// </summary>
    /// <remarks>
    /// The completed review message does not carry rendered Markdown or findings
    /// payloads. It points the writer at the single PR summary comment and the
    /// partitioned review row that now holds the authoritative findings counts.
    /// </remarks>
    private static GitHubCommentTargetSelector CreatePullRequestSummaryTarget(
        CodeReviewRunRequested message
    ) =>
        new(
            OrganizationId: message.OrganizationId,
            RepositoryId: message.RepositoryId,
            PullRequestNumber: message.PullRequestNumber,
            Kind: GitHubCommentTargetKind.PullRequestSummary,
            ScopeKey: GitHubCommentMarkers.PullRequestSummaryScopeKey
        );

    [LoggerMessage(
        EventId = 3210,
        Level = LogLevel.Information,
        Message = "Completed code review. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogReviewCompleted(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId
    );

    [LoggerMessage(
        EventId = 3215,
        Level = LogLevel.Information,
        Message = "Received queued code review run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}, RepositoryId={RepositoryId}, PullRequestRecordId={PullRequestRecordId}"
    )]
    private static partial void LogRunMessageReceived(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId,
        string repositoryId,
        string pullRequestRecordId
    );

    [LoggerMessage(
        EventId = 3217,
        Level = LogLevel.Information,
        Message = "Skipped queued code review because record is terminal. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, Status={Status}"
    )]
    private static partial void LogTerminalReviewSkipped(
        ILogger logger,
        string organizationId,
        string codeReviewId,
        CodeReviewStatus status
    );

    [LoggerMessage(
        EventId = 3225,
        Level = LogLevel.Information,
        Message = "Starting code review execution lease acquisition. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, MaxConcurrentReviews={MaxConcurrentReviews}"
    )]
    private static partial void LogExecutionLeaseAcquisitionStarting(
        ILogger logger,
        string organizationId,
        string codeReviewId,
        int maxConcurrentReviews
    );

    [LoggerMessage(
        EventId = 3226,
        Level = LogLevel.Error,
        Message = "Failed code review execution lease acquisition. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, ErrorType={ErrorType}, ErrorMessage={ErrorMessage}"
    )]
    private static partial void LogExecutionLeaseAcquisitionFailed(
        ILogger logger,
        Exception exception,
        string organizationId,
        string codeReviewId,
        string errorType,
        string errorMessage
    );

    [LoggerMessage(
        EventId = 3220,
        Level = LogLevel.Information,
        Message = "Acquired code review execution lease. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, LeaseId={LeaseId}, SlotIndex={SlotIndex}, MaxConcurrentReviews={MaxConcurrentReviews}"
    )]
    private static partial void LogExecutionLeaseAcquired(
        ILogger logger,
        string organizationId,
        string codeReviewId,
        string leaseId,
        int slotIndex,
        int maxConcurrentReviews
    );

    [LoggerMessage(
        EventId = 3222,
        Level = LogLevel.Information,
        Message = "Starting code review runner. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogRunnerStarting(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId
    );

    [LoggerMessage(
        EventId = 3223,
        Level = LogLevel.Information,
        Message = "Code review runner returned findings. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}, Critical={CriticalFindings}, Major={MajorFindings}, Minor={MinorFindings}, Suggestion={SuggestionFindings}, Comment={CommentFindings}"
    )]
    private static partial void LogRunnerReturned(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId,
        int criticalFindings,
        int majorFindings,
        int minorFindings,
        int suggestionFindings,
        int commentFindings
    );

    [LoggerMessage(
        EventId = 3224,
        Level = LogLevel.Information,
        Message = "Published code review comment signal. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}, Kind={Kind}"
    )]
    private static partial void LogCommentSignalPublished(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId,
        string kind
    );

    [LoggerMessage(
        EventId = 3214,
        Level = LogLevel.Error,
        Message = "Failed code review. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CodeReviewId={CodeReviewId}, ErrorType={ErrorType}, ErrorMessage={ErrorMessage}"
    )]
    private static partial void LogReviewFailed(
        ILogger logger,
        Exception exception,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string codeReviewId,
        string errorType,
        string errorMessage
    );

    [LoggerMessage(
        EventId = 3211,
        Level = LogLevel.Information,
        Message = "Deferred code review because organization capacity is full. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}, MaxConcurrentReviews={MaxConcurrentReviews}, Delay={Delay}"
    )]
    private static partial void LogCapacityDeferred(
        ILogger logger,
        string organizationId,
        string codeReviewId,
        int maxConcurrentReviews,
        TimeSpan delay
    );

    [LoggerMessage(
        EventId = 3212,
        Level = LogLevel.Warning,
        Message = "Failed code review because capacity deferral window expired. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogCapacityDeferralExhausted(
        ILogger logger,
        string organizationId,
        string codeReviewId
    );

    [LoggerMessage(
        EventId = 3213,
        Level = LogLevel.Information,
        Message = "Acknowledged redelivered code review run because execution lease is already held. OrganizationId={OrganizationId}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogExecutionLeaseAlreadyHeld(
        ILogger logger,
        string organizationId,
        string codeReviewId
    );

    [LoggerMessage(
        EventId = 3309,
        Level = LogLevel.Warning,
        Message = "Check run could not be resolved: PR record not found after review completed. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, PullRequestRecordId={PullRequestRecordId}, CodeReviewId={CodeReviewId}"
    )]
    private static partial void LogCheckRunResolvePrMissing(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string pullRequestRecordId,
        string codeReviewId
    );
}

internal sealed class CodeReviewExecutionLeaseLostException(string leaseId)
    : InvalidOperationException($"Code review execution lease {leaseId} was lost.");

internal sealed class CodeReviewActiveLockLostException(
    string organizationId,
    string pullRequestRecordId,
    string codeReviewRecordId
)
    : InvalidOperationException(
        $"Code review active lock was lost. OrganizationId={organizationId}, PullRequestRecordId={pullRequestRecordId}, CodeReviewId={codeReviewRecordId}"
    );
