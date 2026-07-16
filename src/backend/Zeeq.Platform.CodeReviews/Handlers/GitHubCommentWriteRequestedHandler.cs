using System.Diagnostics;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Consumes lightweight GitHub comment write signals and performs the serialized render/write pass.
/// </summary>
/// <remarks>
/// This handler is the orchestration boundary for Zeeq-owned GitHub comments.
/// Producers do not send Markdown and do not update a desired-state table. They
/// send a target, a render kind, and optional authoritative record references.
/// The handler acquires the short-lived target lease, resolves the current live
/// GitHub comment into a DOM, renders only the sections owned by the current
/// signal, and writes the complete DOM-marked Markdown body back to GitHub.
///
/// The lease is a row in an unlogged table, not a database transaction held open
/// during GitHub calls. Renewal runs beside the work task so slow GitHub calls
/// do not let the lease expire silently. If renewal fails, the work token is
/// cancelled and the handler fails transiently; a later queue retry can acquire
/// the target once the old lease expires or is released.
/// </remarks>
[ConfigureConsumer<GitHubCommentWriteRequested>(
    "github.comment.write",
    // ImmediateMessage means a single real channel (no tenant-bucket
    // fan-out), so this value is the real, exact concurrency — bumped to
    // match github.comment.reaction.worker's 4, since comment writes are
    // important feedback and shouldn't be throttled below the reaction path.
    noOfPerformers: 4,
    bufferSize: 8,
    visibleTimeoutSeconds: 420,
    pollIntervalMilliseconds: 50
)]
public sealed partial class GitHubCommentWriteRequestedHandler(
    IDeadLetterWriter deadLetterWriter,
    IGitHubCommentLeaseStore leases,
    IGitHubCommentAnchorStore anchors,
    IGitHubCommentClientFactory commentClients,
    IGitHubCommentResolver resolver,
    IGitHubCommentDomRenderer renderer,
    ICodeReviewRecordStore codeReviews,
    ICodeReviewArtifactStore artifacts,
    CodeReviewXmlOutputValidator xmlValidator,
    CodeReviewRequestLinkFactory linkFactory,
    IGitHubCommentWriter writer,
    GitHubCommentWriteOptions options,
    ILogger<GitHubCommentWriteRequestedHandler> logger
) : ZeeqMessageHandler<GitHubCommentWriteRequested>(deadLetterWriter)
{
    /// <summary>
    /// Handles one queued render/write signal for a GitHub comment target.
    /// </summary>
    /// <remarks>
    /// The method deliberately separates three concerns: acquire the target
    /// lease, run the render/write work with a concurrent renewal loop, and
    /// release the owner-checked lease in <c>finally</c>. The handler never holds
    /// an EF transaction or advisory lock while it talks to GitHub.
    /// </remarks>
    protected override async Task<GitHubCommentWriteRequested> HandleMessageAsync(
        GitHubCommentWriteRequested message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);
        var leaseKey = new GitHubCommentLeaseKey(message.Target);
        var workerId = CreateWorkerId();
        var leaseDuration = ResolveLeaseDuration();
        var targetKey = message.Target.ToStorageKey();

        LogCommentWriteStarted(logger, targetKey, message.Kind, message.SignalId);
        if (!await TryAcquireLeaseAsync(leaseKey, workerId, leaseDuration, cancellationToken))
        {
            activity?.AddEvent(
                [
                    ("github.comment.target", targetKey),
                    ("github.comment.kind", message.Kind),
                    ("github.comment.signal_id", message.SignalId),
                ],
                "github.comment.lease_unavailable"
            );
            LogLeaseUnavailable(logger, targetKey, message.SignalId);

            throw new GitHubCommentLeaseUnavailableException(leaseKey);
        }

        activity?.AddEvent(
            [
                ("github.comment.target", targetKey),
                ("github.comment.kind", message.Kind),
                ("github.comment.signal_id", message.SignalId),
            ],
            "github.comment.lease_acquired"
        );

        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workTask = RenderAndWriteAsync(message, workCts.Token);
        var renewalTask = RunLeaseRenewalAsync(leaseKey, workerId, leaseDuration, workCts);

        try
        {
            await AwaitWorkWithRenewalAsync(workTask, renewalTask, workCts);

            LogCommentWriteWorkCompleted(logger, targetKey, message.Kind, message.SignalId);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested
            )
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);

            activity?.AddEvent(
                [
                    ("github.comment.target", targetKey),
                    ("github.comment.kind", message.Kind),
                    ("github.comment.signal_id", message.SignalId),
                    ("exception.type", ex.GetType().Name),
                    ("exception.message", ex.Message),
                ],
                "github.comment.write_failed"
            );

            LogCommentWriteFailed(
                logger,
                targetKey,
                message.Kind,
                message.SignalId,
                ex.GetType().Name
            );

            throw;
        }
        finally
        {
            await workCts.CancelAsync();
            await IgnoreRenewalShutdownAsync(renewalTask);

            // Release ignores caller cancellation because this is cleanup for a
            // best-effort external write slot. The store is owner-checked, so a
            // stale worker cannot delete a newer worker's lease after takeover.
            LogCommentLeaseReleaseStarting(logger, targetKey, message.Kind, message.SignalId);
            await leases.ReleaseAsync(leaseKey, workerId, CancellationToken.None);
            activity?.AddEvent(
                [
                    ("github.comment.target", targetKey),
                    ("github.comment.kind", message.Kind),
                    ("github.comment.signal_id", message.SignalId),
                ],
                "github.comment.lease_released"
            );
            LogCommentLeaseReleased(logger, targetKey, message.Kind, message.SignalId);
        }

        LogCommentWriteCompleted(logger, targetKey, message.Kind, message.SignalId);
        return message;
    }

    /// <summary>
    /// Attempts to acquire the target lease, waiting briefly when another writer is active.
    /// </summary>
    /// <remarks>
    /// Pull-request webhooks can emit an immediate placeholder comment and then
    /// a completed-review comment update only milliseconds apart. Both target
    /// the same root comment. A single immediate retry loop in Brighter is too
    /// short to model this contention, so the writer waits here for the active
    /// GitHub write to finish before handing the message back to the queue
    /// retry/dead-letter policy.
    /// </remarks>
    private async Task<bool> TryAcquireLeaseAsync(
        GitHubCommentLeaseKey leaseKey,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    )
    {
        if (await leases.TryAcquireAsync(leaseKey, workerId, leaseDuration, cancellationToken))
        {
            return true;
        }

        var startedAt = TimeProvider.System.GetTimestamp();
        var retryDelay =
            options.LeaseAcquireRetryDelay > TimeSpan.Zero
                ? options.LeaseAcquireRetryDelay
                : TimeSpan.FromMilliseconds(250);

        while (
            TimeProvider.System.GetElapsedTime(startedAt) < options.LeaseAcquireTimeout
            && !cancellationToken.IsCancellationRequested
        )
        {
            await Task.Delay(retryDelay, cancellationToken);

            if (await leases.TryAcquireAsync(leaseKey, workerId, leaseDuration, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Waits for the work task while treating renewal as the guard task.
    /// </summary>
    /// <remarks>
    /// Renewal should finish first only when the lease is lost, renewal faults,
    /// or the shared token is cancelled. On lease loss we cancel the work token
    /// and wait for the work task to observe cancellation before the handler
    /// releases its owner-checked lease row.
    /// </remarks>
    private static async Task AwaitWorkWithRenewalAsync(
        Task workTask,
        Task renewalTask,
        CancellationTokenSource workCts
    )
    {
        var completedTask = await Task.WhenAny(workTask, renewalTask);
        if (completedTask == workTask)
        {
            try
            {
                await workTask;
            }
            catch (OperationCanceledException) when (workCts.IsCancellationRequested)
            {
                // Lease loss cancels the work token before throwing from the
                // renewal loop. The work task can observe cancellation before
                // the renewal task transitions to faulted, so await the guard
                // task and surface its exception when it is the cancellation
                // source.
                await renewalTask;
                throw;
            }

            return;
        }

        try
        {
            await renewalTask;
        }
        finally
        {
            await workCts.CancelAsync();
            await IgnoreCanceledWorkAsync(workTask, workCts.Token);
        }
    }

    /// <summary>
    /// Renews the target lease until the work token is cancelled.
    /// </summary>
    /// <remarks>
    /// The loop sleeps for half of the lease duration. That keeps at least half
    /// of the lease window available after each successful renewal and makes the
    /// row self-heal after process death because no renewal occurs.
    /// </remarks>
    private async Task RunLeaseRenewalAsync(
        GitHubCommentLeaseKey key,
        string workerId,
        TimeSpan leaseDuration,
        CancellationTokenSource workCts
    )
    {
        var interval = ResolveRenewalInterval(leaseDuration);

        while (true)
        {
            await Task.Delay(interval, workCts.Token);

            var renewed = await leases.RenewAsync(key, workerId, leaseDuration, workCts.Token);

            if (renewed)
            {
                continue;
            }

            await workCts.CancelAsync();

            throw new GitHubCommentLeaseLostException(key);
        }
    }

    /// <summary>
    /// Resolves the current GitHub DOM, renders the requested sections, and writes the body.
    /// </summary>
    /// <remarks>
    /// The durable anchor is only a fast lookup pointer. The current GitHub
    /// comment body remains the source of document state, so this method always
    /// resolves or scans GitHub before rendering. When a scan recovers a comment
    /// id, the anchor is repaired before the final write path continues.
    /// </remarks>
    private async Task RenderAndWriteAsync(
        GitHubCommentWriteRequested message,
        CancellationToken cancellationToken
    )
    {
        var targetKey = message.Target.ToStorageKey();
        var anchor = await anchors.FindAsync(message.Target, cancellationToken);

        ZeeqTelemetry.AddEvent(
            [
                ("github.comment.target", targetKey),
                ("github.comment.kind", message.Kind),
                ("github.comment.anchor_present", anchor is not null),
                ("github.comment.anchor_id", anchor?.GitHubCommentId),
            ],
            "github.comment.anchor_resolved"
        );

        var client = await commentClients.CreateForOrganizationAsync(
            message.OrganizationId,
            cancellationToken
        );

        var resolution = await resolver.ResolveAsync(
            client,
            message.Target,
            message.OwnerQualifiedRepoName,
            anchor?.GitHubCommentId,
            cancellationToken
        );

        var anchorRepaired =
            resolution is not null && anchor?.GitHubCommentId != resolution.CommentId;

        ZeeqTelemetry.AddEvent(
            [
                ("github.comment.target", targetKey),
                ("github.comment.kind", message.Kind),
                ("github.comment.exists", resolution is not null),
                ("github.comment.comment_id", resolution?.CommentId),
                ("github.comment.anchor_repaired", anchorRepaired),
            ],
            "github.comment.dom_resolved"
        );

        if (resolution is not null && anchor?.GitHubCommentId != resolution.CommentId)
        {
            LogCommentAnchorRepairStarting(logger, targetKey, message.Kind, resolution.CommentId);

            await anchors.UpsertResolvedAsync(
                message.Target,
                message.OwnerQualifiedRepoName,
                resolution.CommentId,
                cancellationToken
            );

            LogCommentAnchorPersisted(logger, targetKey, message.Kind, resolution.CommentId);
        }

        var dom = resolution?.Dom ?? GitHubCommentDom.Empty(message.Target);

        if (ShouldSkip(message, dom))
        {
            ZeeqTelemetry.AddEvent(
                [("github.comment.target", targetKey), ("github.comment.kind", message.Kind)],
                "github.comment.render_skipped"
            );

            LogCommentRenderSkipped(logger, targetKey, message.Kind);

            return;
        }

        var context = await LoadRenderContextAsync(message, cancellationToken);

        LogCommentRenderContextLoaded(
            logger,
            targetKey,
            message.Kind,
            context.Review?.Id,
            context.Review?.Status,
            context.Findings is not null,
            context.FindingsLoadError is not null
        );

        var body = renderer.Render(message.Kind, message.Clear, context, dom);

        ZeeqTelemetry.AddEvent(
            [
                ("github.comment.target", targetKey),
                ("github.comment.kind", message.Kind),
                ("github.comment.body_char_count", body.Length),
                ("github.comment.clear_marker_count", message.Clear.Count),
                ("code_review.id", context.Review?.Id),
                ("code_review.findings.loaded", context.Findings is not null),
                ("code_review.findings.load_error", context.FindingsLoadError),
            ],
            "github.comment.rendered"
        );

        var commentId = await writer.UpsertAsync(
            client,
            message.Target,
            message.OwnerQualifiedRepoName,
            resolution?.CommentId,
            body,
            cancellationToken
        );

        LogCommentGitHubUpsertReturned(logger, targetKey, message.Kind, commentId);

        await anchors.UpsertResolvedAsync(
            message.Target,
            message.OwnerQualifiedRepoName,
            commentId,
            cancellationToken
        );

        LogCommentAnchorPersisted(logger, targetKey, message.Kind, commentId);

        ZeeqTelemetry.AddEvent(
            [
                ("github.comment.target", targetKey),
                ("github.comment.kind", message.Kind),
                ("github.comment.comment_id", commentId),
            ],
            "github.comment.upsert_completed"
        );

        LogCommentUpsertCompleted(logger, targetKey, message.Kind, commentId);
    }

    /// <summary>
    /// Hydrates all data needed by synchronous GitHub comment section renderers.
    /// </summary>
    /// <remarks>
    /// The DOM renderer is intentionally pure: it should not read the database,
    /// open artifact streams, deserialize XML, or mint signed frontend links.
    /// This method keeps those I/O concerns in the comment writer, directly
    /// before the render pass, so a completed-review message either renders from
    /// a validated artifact or carries a controlled load error that the status
    /// section can display without throwing from a section renderer.
    /// </remarks>
    private async Task<CodeReviewCommentRenderContext> LoadRenderContextAsync(
        GitHubCommentWriteRequested message,
        CancellationToken cancellationToken
    )
    {
        var review = await LoadCodeReviewAsync(message, cancellationToken);
        var actionLinks = BuildActionLinks(message, review);
        var renderedAtUtc = DateTimeOffset.UtcNow;
        var showNoice = ShouldShowNoice(actionLinks);

        // Deserialize once and pass into EVERY returned context (including the early-return
        // branches) so the sources section renders even for zero-finding / no-agents reviews.
        // Best-effort: a malformed payload deserializes to null (no section), never throwing.
        var sourceTelemetry = CodeReviewSourceTelemetrySerializer.Deserialize(
            review?.SourceTelemetryPayload
        );

        if (!ShouldLoadFindings(message.Kind) || review is null)
        {
            return new(
                Review: review,
                FindingsXml: null,
                Findings: null,
                FindingsLoadError: null,
                ActionLinks: actionLinks,
                RenderedAtUtc: renderedAtUtc,
                ShowNoice: showNoice,
                SourceTelemetry: sourceTelemetry
            );
        }

        if (string.IsNullOrWhiteSpace(review.FindingsStorageUri))
        {
            return new(
                Review: review,
                FindingsXml: null,
                Findings: null,
                FindingsLoadError: "Completed code review is missing a findings artifact URI.",
                ActionLinks: actionLinks,
                RenderedAtUtc: renderedAtUtc,
                ShowNoice: showNoice,
                SourceTelemetry: sourceTelemetry
            );
        }

        try
        {
            var findingsXml = await ReadFindingsXmlAsync(
                review.FindingsStorageUri,
                cancellationToken
            );
            var validation = xmlValidator.Validate(findingsXml);

            if (!validation.IsValid || validation.Output is null)
            {
                return new(
                    Review: review,
                    FindingsXml: findingsXml,
                    Findings: null,
                    FindingsLoadError: validation.ErrorMessage
                        ?? "Findings XML could not be validated.",
                    ActionLinks: actionLinks,
                    RenderedAtUtc: renderedAtUtc,
                    ShowNoice: showNoice,
                    SourceTelemetry: sourceTelemetry
                );
            }

            return new(
                Review: review,
                FindingsXml: findingsXml,
                Findings: validation.Output,
                FindingsLoadError: null,
                ActionLinks: actionLinks,
                RenderedAtUtc: renderedAtUtc,
                ShowNoice: showNoice,
                SourceTelemetry: sourceTelemetry
            );
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested
            )
        {
            return new(
                Review: review,
                FindingsXml: null,
                Findings: null,
                FindingsLoadError: $"Findings artifact could not be loaded: {ex.Message}",
                ActionLinks: actionLinks,
                RenderedAtUtc: renderedAtUtc,
                ShowNoice: showNoice,
                SourceTelemetry: sourceTelemetry
            );
        }
    }

    private async Task<string> ReadFindingsXmlAsync(
        string findingsStorageUri,
        CancellationToken cancellationToken
    )
    {
        await using var stream = await artifacts.OpenFindingsAsync(
            findingsStorageUri,
            cancellationToken
        );
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true
        );

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private CodeReviewCommentActionLinks BuildActionLinks(
        GitHubCommentWriteRequested message,
        CodeReviewRecord? review
    )
    {
        var noiceImageUrl = linkFactory.BuildPublicAssetLink("noice.webp");

        if (message.Kind == "draft_prompt")
        {
            var link = linkFactory.BuildInitialReviewLink(
                message.OrganizationId,
                message.TeamId,
                message.RepositoryId,
                message.OwnerQualifiedRepoName,
                message.PullRequestNumber
            );

            return new(RequestReviewUrl: link.Url, NoiceImageUrl: noiceImageUrl);
        }

        // Completed review kinds always get a deep link to the single-review view,
        // independent of remaining budget (unlike the re-request link).
        var viewReviewUrl =
            review is not null
            && message.Kind
                is "review_completed"
                    or "stub_review_completed"
                    or "no_agents_activated"
                ? linkFactory.BuildSingleReviewLink(review, CodeReviewSingleViewMode.Pr)
                : null;

        if (review is null || review.RemainingReviewBudget <= 0)
        {
            return new(NoiceImageUrl: noiceImageUrl, ViewReviewUrl: viewReviewUrl);
        }

        if (
            message.Kind
            is not (
                "review_completed"
                or "stub_review_completed"
                or "review_failed"
                or "no_agents_activated"
            )
        )
        {
            return new(NoiceImageUrl: noiceImageUrl, ViewReviewUrl: viewReviewUrl);
        }

        var existingLink = linkFactory.BuildExistingReviewLink(review);

        return new(
            RequestReviewUrl: existingLink.Url,
            NoiceImageUrl: noiceImageUrl,
            ViewReviewUrl: viewReviewUrl
        );
    }

    private static bool ShouldLoadFindings(string kind) =>
        kind is "review_completed" or "stub_review_completed" or "no_agents_activated";

    private static bool ShouldShowNoice(CodeReviewCommentActionLinks actionLinks) =>
        !string.IsNullOrWhiteSpace(actionLinks.NoiceImageUrl) && Random.Shared.NextSingle() >= 0.5;

    /// <summary>
    /// Loads the optional review record referenced by the queue message.
    /// </summary>
    /// <remarks>
    /// Both id and partition timestamp must travel together. A message with only
    /// one half of the pair is malformed because the code review table is
    /// partitioned and callers should never scan partitions to hydrate render
    /// content.
    /// </remarks>
    private async Task<CodeReviewRecord?> LoadCodeReviewAsync(
        GitHubCommentWriteRequested message,
        CancellationToken cancellationToken
    )
    {
        if (message.CodeReviewRecordId is null && message.CodeReviewCreatedAtUtc is null)
        {
            return null;
        }

        if (message.CodeReviewRecordId is null || message.CodeReviewCreatedAtUtc is null)
        {
            throw new InvalidOperationException(
                "GitHub comment write messages must include both CodeReviewRecordId and CodeReviewCreatedAtUtc when referencing a review."
            );
        }

        var review = await codeReviews.FindAsync(
            message.CodeReviewRecordId,
            message.CodeReviewCreatedAtUtc.Value,
            cancellationToken
        );

        if (review is null)
        {
            throw new InvalidOperationException(
                $"Referenced code review was not found. Id={message.CodeReviewRecordId}, CreatedAtUtc={message.CodeReviewCreatedAtUtc.Value:O}"
            );
        }

        return review;
    }

    /// <summary>
    /// Runs the conservative stale-message sentinel.
    /// </summary>
    /// <remarks>
    /// NOTE: The plan explicitly rejected the old "findings section exists"
    /// sentinel because it would suppress valid updates for a newer review
    /// generation. Until source-generation metadata is added to rendered
    /// sections, the safe rule is to never skip based only on section presence.
    /// </remarks>
    private static bool ShouldSkip(GitHubCommentWriteRequested message, GitHubCommentDom dom) =>
        false;

    private TimeSpan ResolveLeaseDuration()
    {
        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("GitHub comment lease duration must be positive.");
        }

        return options.LeaseDuration;
    }

    private static TimeSpan ResolveRenewalInterval(TimeSpan leaseDuration)
    {
        var interval = leaseDuration / 2;
        return interval > TimeSpan.Zero ? interval : TimeSpan.FromMilliseconds(1);
    }

    private static async Task IgnoreRenewalShutdownAsync(Task renewalTask)
    {
        try
        {
            await renewalTask;
        }
        catch (OperationCanceledException)
        {
            // The normal path cancels the renewal loop after the work task ends.
        }
        catch
        {
            // Renewal faults are observed by AwaitWorkWithRenewalAsync when
            // renewal wins the race. During cleanup we must not throw before
            // releasing the owner-checked lease row.
        }
    }

    private static async Task IgnoreCanceledWorkAsync(Task workTask, CancellationToken workToken)
    {
        try
        {
            await workTask;
        }
        catch (OperationCanceledException) when (workToken.IsCancellationRequested)
        {
            // Renewal loss cancels in-flight work; this is the expected shutdown path.
        }
    }

    private static string CreateWorkerId() =>
        $"github-comment:{Environment.MachineName}:{Guid.CreateVersion7():N}";

    private static Activity? StartActivity(GitHubCommentWriteRequested message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);

        return ZeeqTelemetry.Tracer.StartActivity(
            "github.comment.write",
            ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("github.repo", message.OwnerQualifiedRepoName),
                new("organization.id", message.OrganizationId),
                new("github.comment.target", message.Target.ToStorageKey()),
                new("github.comment.kind", message.Kind),
                new("github.comment.signal_id", message.SignalId),
            ]
        );
    }

    [LoggerMessage(
        EventId = 3220,
        Level = LogLevel.Debug,
        Message = "GitHub comment lease unavailable. Target={TargetKey}, SignalId={SignalId}"
    )]
    private static partial void LogLeaseUnavailable(
        ILogger logger,
        string targetKey,
        string signalId
    );

    [LoggerMessage(
        EventId = 3227,
        Level = LogLevel.Information,
        Message = "Started GitHub comment write. Target={TargetKey}, Kind={Kind}, SignalId={SignalId}"
    )]
    private static partial void LogCommentWriteStarted(
        ILogger logger,
        string targetKey,
        string kind,
        string signalId
    );

    [LoggerMessage(
        EventId = 3228,
        Level = LogLevel.Information,
        Message = "Completed GitHub comment render/write work task. Target={TargetKey}, Kind={Kind}, SignalId={SignalId}"
    )]
    private static partial void LogCommentWriteWorkCompleted(
        ILogger logger,
        string targetKey,
        string kind,
        string signalId
    );

    [LoggerMessage(
        EventId = 3229,
        Level = LogLevel.Information,
        Message = "Releasing GitHub comment lease. Target={TargetKey}, Kind={Kind}, SignalId={SignalId}"
    )]
    private static partial void LogCommentLeaseReleaseStarting(
        ILogger logger,
        string targetKey,
        string kind,
        string signalId
    );

    [LoggerMessage(
        EventId = 3230,
        Level = LogLevel.Information,
        Message = "Released GitHub comment lease. Target={TargetKey}, Kind={Kind}, SignalId={SignalId}"
    )]
    private static partial void LogCommentLeaseReleased(
        ILogger logger,
        string targetKey,
        string kind,
        string signalId
    );

    [LoggerMessage(
        EventId = 3221,
        Level = LogLevel.Information,
        Message = "Completed GitHub comment write. Target={TargetKey}, Kind={Kind}, SignalId={SignalId}"
    )]
    private static partial void LogCommentWriteCompleted(
        ILogger logger,
        string targetKey,
        string kind,
        string signalId
    );

    [LoggerMessage(
        EventId = 3222,
        Level = LogLevel.Error,
        Message = "Failed GitHub comment write. Target={TargetKey}, Kind={Kind}, SignalId={SignalId}, ErrorType={ErrorType}"
    )]
    private static partial void LogCommentWriteFailed(
        ILogger logger,
        string targetKey,
        string kind,
        string signalId,
        string errorType
    );

    [LoggerMessage(
        EventId = 3235,
        Level = LogLevel.Information,
        Message = "Persisting repaired GitHub comment anchor. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}"
    )]
    private static partial void LogCommentAnchorRepairStarting(
        ILogger logger,
        string targetKey,
        string kind,
        long commentId
    );

    [LoggerMessage(
        EventId = 3236,
        Level = LogLevel.Information,
        Message = "Persisted GitHub comment anchor. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}"
    )]
    private static partial void LogCommentAnchorPersisted(
        ILogger logger,
        string targetKey,
        string kind,
        long commentId
    );

    [LoggerMessage(
        EventId = 3224,
        Level = LogLevel.Information,
        Message = "Skipped GitHub comment render. Target={TargetKey}, Kind={Kind}"
    )]
    private static partial void LogCommentRenderSkipped(
        ILogger logger,
        string targetKey,
        string kind
    );

    [LoggerMessage(
        EventId = 3238,
        Level = LogLevel.Information,
        Message = "Loaded GitHub comment render context. Target={TargetKey}, Kind={Kind}, CodeReviewId={CodeReviewId}, ReviewStatus={ReviewStatus}, FindingsLoaded={FindingsLoaded}, HasFindingsLoadError={HasFindingsLoadError}"
    )]
    private static partial void LogCommentRenderContextLoaded(
        ILogger logger,
        string targetKey,
        string kind,
        string? codeReviewId,
        CodeReviewStatus? reviewStatus,
        bool findingsLoaded,
        bool hasFindingsLoadError
    );

    [LoggerMessage(
        EventId = 3241,
        Level = LogLevel.Information,
        Message = "GitHub comment upsert returned. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}"
    )]
    private static partial void LogCommentGitHubUpsertReturned(
        ILogger logger,
        string targetKey,
        string kind,
        long commentId
    );

    [LoggerMessage(
        EventId = 3226,
        Level = LogLevel.Information,
        Message = "Completed GitHub comment upsert. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}"
    )]
    private static partial void LogCommentUpsertCompleted(
        ILogger logger,
        string targetKey,
        string kind,
        long commentId
    );
}

/// <summary>
/// Raised when another worker currently owns the GitHub comment target lease.
/// </summary>
/// <remarks>
/// The exception is intentionally transient. The message queue retry pipeline
/// should redeliver the signal after the current lease holder finishes or the
/// short lease expires.
/// </remarks>
public sealed class GitHubCommentLeaseUnavailableException(GitHubCommentLeaseKey key)
    : InvalidOperationException($"GitHub comment lease is unavailable for {key}.")
{
    /// <summary>Lease key that could not be acquired.</summary>
    public GitHubCommentLeaseKey Key { get; } = key;
}

/// <summary>
/// Raised when the handler loses a lease it had acquired earlier in the write pass.
/// </summary>
/// <remarks>
/// Lease loss means this worker should stop before it writes stale DOM state
/// over another worker's output. The handler cancels its work token before
/// surfacing this exception to the queue retry pipeline.
/// </remarks>
public sealed class GitHubCommentLeaseLostException(GitHubCommentLeaseKey key)
    : InvalidOperationException($"GitHub comment lease was lost for {key}.")
{
    /// <summary>Lease key that was lost during renewal.</summary>
    public GitHubCommentLeaseKey Key { get; } = key;
}
