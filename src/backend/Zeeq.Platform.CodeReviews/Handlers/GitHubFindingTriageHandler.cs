using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Dispatches triage commands from GitHub comments.</summary>
public interface IGitHubFindingTriageHandler
{
    /// <summary>Returns true when the comment is a triage command.</summary>
    Task<bool> HandleAsync(
        string orgId,
        string? teamId,
        string repositoryId,
        string repo,
        long prNumber,
        long commentId,
        string? body,
        string? author,
        string? association,
        DateTimeOffset? createdAt,
        string? sourceUrl,
        string action,
        string deliveryId,
        ZeeqTraceContext trace,
        GitHubCommentReactionTargetKind targetKind,
        CancellationToken ct
    );
}

/// <summary>Handles triage commands after GitHub webhook normalization.</summary>
public sealed class GitHubFindingTriageHandler(
    IActiveGitHubAliasStore aliases,
    ICodeReviewFindingTriageService triage,
    IZeeqMessagePublisher publisher,
    IGitHubCommentClientFactory clients,
    CodeReviewSettings settings,
    IGitHubCommentLeaseStore leases
) : IGitHubFindingTriageHandler
{
    /// <summary>Validates and dispatches a command. Returns false for non-triage feedback.</summary>
    public async Task<bool> HandleAsync(
        string orgId,
        string? teamId,
        string repositoryId,
        string repo,
        long prNumber,
        long commentId,
        string? body,
        string? author,
        string? association,
        DateTimeOffset? createdAt,
        string? sourceUrl,
        string action,
        string deliveryId,
        ZeeqTraceContext trace,
        GitHubCommentReactionTargetKind targetKind,
        CancellationToken ct
    )
    {
        if (
            !GitHubFeedbackCommandPolicy.TryParseCommand(body, out var command)
            || command
                is not (GitHubFeedbackCommand.Triage or GitHubFeedbackCommand.TriageParseError)
        )
            return false;
        if (
            !string.Equals(action, "created", StringComparison.OrdinalIgnoreCase)
            || commentId <= 0
            || prNumber <= 0
            || prNumber > int.MaxValue
        )
            return true;
        var login = author?.Trim().TrimStart('@');
        if (
            string.IsNullOrEmpty(login)
            || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
            || string.Equals(login, "zeeq", StringComparison.OrdinalIgnoreCase)
            || string.Equals(login, settings.AgentIdentity, StringComparison.OrdinalIgnoreCase)
        )
            return true;

        string? error = null;
        var alias = association?.ToUpperInvariant() is "OWNER" or "MEMBER" or "COLLABORATOR"
            ? await aliases.FindActiveGitHubAliasAsync(orgId, login.ToLowerInvariant(), ct)
            : null;
        if (alias is null)
            error =
                "Link your GitHub alias to an active Zeeq organization membership and ensure you have collaborator access.";
        else if (createdAt is null)
            error = "The comment timestamp is missing; submit a new command.";
        else if (command == GitHubFeedbackCommand.TriageParseError)
            error =
                "Use `/zeeq fp <ref> <reason>`, `/zeeq ignore <ref> <reason>`, `/zeeq confirm <ref>`, or `/zeeq clear <ref>`.";
        else
        {
            var parsed = GitHubFeedbackCommandPolicy.ParseTriage(body)!.Value;
            var (operation, disposition) = parsed.Verb switch
            {
                "confirm" => (FindingTriageOperation.Confirm, (CodeReviewFindingDisposition?)null),
                "clear" or "undo" => (
                    FindingTriageOperation.Clear,
                    (CodeReviewFindingDisposition?)null
                ),
                "fp" or "false-positive" => (
                    FindingTriageOperation.Set,
                    (CodeReviewFindingDisposition?)CodeReviewFindingDisposition.FalsePositive
                ),
                "ignore" or "defer" or "wontfix" => (
                    FindingTriageOperation.Set,
                    (CodeReviewFindingDisposition?)CodeReviewFindingDisposition.Deferred
                ),
                _ => throw new InvalidOperationException("Unsupported parsed triage verb."),
            };
            var outcome = await triage.TriageAsync(
                new(
                    orgId,
                    new FindingTriageTarget.ByPullRequest(repositoryId, (int)prNumber),
                    parsed.Reference,
                    operation,
                    disposition,
                    parsed.Reason,
                    CodeReviewFindingTriageChannel.GitHub,
                    alias.UserId,
                    SourceEventAtUtc: createdAt,
                    SourceUrl: sourceUrl,
                    GitHubAliasId: alias.Id
                ),
                ct
            );
            error = outcome switch
            {
                FindingTriageOutcome.Applied => null,
                FindingTriageOutcome.NotFound =>
                    "Finding not found. Copy a reference from the latest review summary.",
                FindingTriageOutcome.Ambiguous x => "Finding reference is ambiguous: "
                    + string.Join(
                        ", ",
                        x.Candidates.Select(c => $"`{c.Reference}` · `{c.SemanticId}`")
                    ),
                FindingTriageOutcome.Forbidden x => x.Reason,
                FindingTriageOutcome.IgnoredStale =>
                    "This command is older than the current triage state; submit a new command.",
                FindingTriageOutcome.Invalid x => string.Join(" ", x.Errors),
            };
        }
        await publisher.PublishAsync(
            new GitHubCommentReactionRequested
            {
                OrganizationId = orgId,
                TeamId = teamId,
                RepositoryId = repositoryId,
                OwnerQualifiedRepoName = repo,
                PullRequestNumber = (int)prNumber,
                Target = new(targetKind, commentId),
                ReactionContent = error is null
                    ? GitHubCommentReactionContent.PlusOne
                    : GitHubCommentReactionContent.Confused,
                SignalId = deliveryId,
                TraceContext = trace,
            },
            ct
        );
        if (error is null)
        {
            await publisher.PublishAsync(
                new GitHubCommentWriteRequested
                {
                    OrganizationId = orgId,
                    TeamId = teamId,
                    RepositoryId = repositoryId,
                    OwnerQualifiedRepoName = repo,
                    PullRequestNumber = (int)prNumber,
                    Target = new(
                        orgId,
                        repositoryId,
                        (int)prNumber,
                        GitHubCommentTargetKind.PullRequestSummary,
                        GitHubCommentMarkers.PullRequestSummaryScopeKey
                    ),
                    Kind = GitHubCommentKinds.ReviewCompleted,
                    RenderLatestForPullRequest = true,
                    SignalId = deliveryId,
                    TraceContext = trace,
                },
                ct
            );
        }
        else
        {
            var key = new GitHubCommentLeaseKey(
                new(
                    orgId,
                    repositoryId,
                    (int)prNumber,
                    targetKind == GitHubCommentReactionTargetKind.IssueComment
                        ? GitHubCommentTargetKind.StandaloneIssueComment
                        : GitHubCommentTargetKind.ReviewThread,
                    $"triage-error:{targetKind}:{commentId}"
                )
            );
            var worker = Guid.CreateVersion7().ToString("N");
            var duration = TimeSpan.FromMinutes(7);
            if (!await leases.TryAcquireAsync(key, worker, duration, ct))
                throw new GitHubCommentLeaseUnavailableException(key);
            using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var renewal = RenewLeaseAsync(key, worker, duration, workCts);
            try
            {
                await PostErrorAsync(
                    orgId,
                    repo,
                    (int)prNumber,
                    commentId,
                    targetKind,
                    error,
                    key,
                    worker,
                    duration,
                    workCts.Token
                );
                if (renewal.IsFaulted)
                    await renewal;
            }
            finally
            {
                await workCts.CancelAsync();
                try
                {
                    await renewal;
                }
                catch (OperationCanceledException) when (workCts.IsCancellationRequested) { }
                finally
                {
                    await leases.ReleaseAsync(key, worker, CancellationToken.None);
                }
            }
        }
        return true;
    }

    private async Task RenewLeaseAsync(
        GitHubCommentLeaseKey key,
        string worker,
        TimeSpan duration,
        CancellationTokenSource workCts
    )
    {
        try
        {
            while (true)
            {
                await Task.Delay(duration / 2, workCts.Token);
                if (!await leases.RenewAsync(key, worker, duration, workCts.Token))
                    throw new GitHubCommentLeaseUnavailableException(key);
            }
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException || !workCts.IsCancellationRequested)
        {
            await workCts.CancelAsync();
            throw;
        }
    }

    private async Task PostErrorAsync(
        string orgId,
        string repo,
        int prNumber,
        long commentId,
        GitHubCommentReactionTargetKind targetKind,
        string error,
        GitHubCommentLeaseKey key,
        string worker,
        TimeSpan duration,
        CancellationToken ct
    )
    {
        var client = await clients.CreateForOrganizationAsync(orgId, ct);
        var marker = $"<!-- zeeq:triage-error:{targetKind}:{commentId} -->";
        // Review-thread replies are GitHub review comments, and are returned by
        // the review-comment listing; issue timeline replies use the issue listing.
        var candidates =
            targetKind == GitHubCommentReactionTargetKind.IssueComment
                ? client.EnumerateIssueCommentsAsync(repo, prNumber, ct)
                : client.EnumeratePullRequestReviewCommentsAsync(repo, prNumber, ct);
        await foreach (var existing in candidates.WithCancellation(ct))
            if (existing.Body.Contains(marker, StringComparison.Ordinal))
                return;
        ct.ThrowIfCancellationRequested();
        if (!await leases.RenewAsync(key, worker, duration, ct))
            throw new GitHubCommentLeaseUnavailableException(key);
        ct.ThrowIfCancellationRequested();
        var reply = $"{marker}\n{error}";
        if (targetKind == GitHubCommentReactionTargetKind.IssueComment)
            await client.CreateIssueCommentAsync(repo, prNumber, reply, ct);
        else
            await client.CreatePullRequestReviewReplyAsync(repo, prNumber, commentId, reply, ct);
    }
}
