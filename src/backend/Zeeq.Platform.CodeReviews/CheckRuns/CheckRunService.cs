using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Manages GitHub check-run lifecycle for code-review pull requests.
/// </summary>
/// <remarks>
/// The service does not gate on <see cref="CodeRepositoryReviewCheckRunConfiguration.IsEnabled"/>
/// in <see cref="MarkPendingAsync"/> or <see cref="ResolveFromReviewAsync"/>; callers are
/// responsible for loading the repository configuration and calling only when appropriate.
///
/// Every GitHub call is wrapped so a check-run failure never fails the review,
/// webhook, or bypass operation. Failures are logged at Warning level with the
/// organization, repository, and check-run context.
/// </remarks>
public sealed partial class CheckRunService(
    ICheckRunClient client,
    IPullRequestLookupStore lookups,
    IPullRequestRecordStore pullRequests,
    ICodeRepositoryStore repositories,
    CodeReviewRequestLinkFactory linkFactory,
    ILogger<CheckRunService> logger
) : ICheckRunService
{
    /// <inheritdoc />
    public async Task MarkPendingAsync(PullRequestRecord pr, CancellationToken ct)
    {
        using var activity = ZeeqTelemetry.Trace(
            [
                ("organization.id", pr.OrganizationId),
                ("github.repo", pr.OwnerQualifiedRepoName),
                ("pull_request.number", pr.PullRequestNumber),
                ("github.check_run.source", "review"),
            ],
            "code_review.check_run.mark_pending"
        );

        var write = new CheckRunWrite(
            Name: CheckRunConstants.ZeeqCheckRunName,
            HeadSha: pr.HeadSha,
            Status: CheckRunStatusKind.InProgress,
            Conclusion: null,
            Title: "Zeeq code review in progress",
            Summary: "The Zeeq code review is running on this commit.",
            DetailsUrl: null,
            IncludeBypassAction: false
        );

        try
        {
            var existingState = pr.CheckRunState;
            var existingForSameCommit =
                existingState is not null
                && string.Equals(existingState.HeadSha, pr.HeadSha, StringComparison.Ordinal);

            long checkRunId;
            if (existingForSameCommit)
            {
                // A check run already exists for this commit; reset it to in_progress rather than
                // creating a second one. Creating a new run orphans the existing ID in GitHub,
                // causing future bypasses to clear the wrong check run.
                await client.UpdateAsync(
                    pr.OrganizationId,
                    pr.OwnerQualifiedRepoName,
                    existingState!.CheckRunId,
                    write,
                    ct
                );
                checkRunId = existingState.CheckRunId;
                // Reset persisted state to match the in_progress update sent to GitHub. Without
                // this, a previously bypassed run (State=Removed, RemovedBy set) would be upserted
                // with stale values even though GitHub now shows it as in_progress again.
                pr.CheckRunState!.State = CheckRunBlockState.Blocking;
                pr.CheckRunState.RemovedBy = null;
                pr.CheckRunState.RemovedAtUtc = null;
            }
            else
            {
                checkRunId = await client.CreateAsync(
                    pr.OrganizationId,
                    pr.OwnerQualifiedRepoName,
                    write,
                    ct
                );
                pr.CheckRunState = new()
                {
                    CheckRunId = checkRunId,
                    HeadSha = pr.HeadSha,
                    State = CheckRunBlockState.Blocking,
                };
            }

            await pullRequests.UpsertAsync(pr, ct);

            LogCheckRunPendingPosted(
                logger,
                pr.OrganizationId,
                pr.OwnerQualifiedRepoName,
                pr.PullRequestNumber,
                pr.HeadSha,
                checkRunId
            );

            activity?.AddEvent(
                [
                    ("github.check_run.id", checkRunId),
                    ("github.check_run.conclusion", "in_progress"),
                ],
                "code_review.check_run.pending_created"
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckRunPendingFailed(
                logger,
                ex,
                pr.OrganizationId,
                pr.OwnerQualifiedRepoName,
                pr.PullRequestNumber,
                pr.HeadSha
            );
        }
    }

    /// <inheritdoc />
    public async Task ResolveFromReviewAsync(
        CodeReviewRecord review,
        PullRequestRecord pr,
        CodeRepositoryReviewCheckRunConfiguration config,
        CancellationToken ct
    )
    {
        if (!config.IsEnabled)
        {
            return;
        }

        using var activity = ZeeqTelemetry.Trace(
            [
                ("organization.id", pr.OrganizationId),
                ("github.repo", pr.OwnerQualifiedRepoName),
                ("pull_request.number", pr.PullRequestNumber),
                ("github.check_run.source", "review"),
                ("code_review.findings.critical", review.CriticalFindings),
                ("code_review.findings.major", review.MajorFindings),
                ("code_review.status", review.Status),
            ],
            "code_review.check_run.resolve"
        );

        var (conclusion, state, title, summary) = DetermineResolution(review, config);
        var blocking = state == CheckRunBlockState.Blocking;

        activity?.AddEvent(
            [
                ("github.check_run.conclusion", conclusion?.ToString()),
                ("github.check_run.blocking", blocking),
            ],
            "code_review.check_run_evaluated"
        );

        var detailsUrl = BuildPrViewLink(pr);

        var write = new CheckRunWrite(
            Name: CheckRunConstants.ZeeqCheckRunName,
            HeadSha: pr.HeadSha,
            Status: CheckRunStatusKind.Completed,
            Conclusion: conclusion,
            Title: title,
            Summary: summary,
            DetailsUrl: detailsUrl,
            IncludeBypassAction: blocking
        );

        try
        {
            var existingState = pr.CheckRunState;
            if (
                existingState is not null
                && string.Equals(existingState.HeadSha, pr.HeadSha, StringComparison.Ordinal)
            )
            {
                await client.UpdateAsync(
                    pr.OrganizationId,
                    pr.OwnerQualifiedRepoName,
                    existingState.CheckRunId,
                    write,
                    ct
                );
            }
            else
            {
                var checkRunId = await client.CreateAsync(
                    pr.OrganizationId,
                    pr.OwnerQualifiedRepoName,
                    write,
                    ct
                );
                pr.CheckRunState = new() { CheckRunId = checkRunId, HeadSha = pr.HeadSha };
            }

            pr.CheckRunState!.State = state;
            if (blocking)
            {
                pr.CheckRunState.RemovedBy = null;
                pr.CheckRunState.RemovedAtUtc = null;
            }
            else
            {
                pr.CheckRunState.RemovedBy = "zeeq:auto-cleared";
                pr.CheckRunState.RemovedAtUtc = DateTimeOffset.UtcNow;
            }

            await pullRequests.UpsertAsync(pr, ct);

            LogCheckRunResolved(
                logger,
                pr.OrganizationId,
                pr.OwnerQualifiedRepoName,
                pr.PullRequestNumber,
                pr.HeadSha,
                conclusion?.ToString(),
                blocking
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckRunResolveFailed(
                logger,
                ex,
                pr.OrganizationId,
                pr.OwnerQualifiedRepoName,
                pr.PullRequestNumber,
                pr.HeadSha
            );
        }
    }

    /// <inheritdoc />
    public async Task<CheckRunBypassOutcome> BypassAsync(
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        string removedBy,
        CancellationToken ct
    )
    {
        using var activity = ZeeqTelemetry.Trace(
            [
                ("organization.id", organizationId),
                ("pull_request.number", pullRequestNumber),
                ("github.check_run.source", "bypass"),
                ("github.check_run.removed_by", removedBy),
            ],
            "code_review.check_run.bypass"
        );

        var repository = await repositories.FindActiveForOrganizationAsync(
            organizationId,
            repositoryId,
            ct
        );
        if (repository is null)
        {
            LogCheckRunBypassPrNotFound(logger, organizationId, repositoryId, pullRequestNumber);
            return CheckRunBypassOutcome.PrNotFound;
        }

        // NOTE: Two-step resolution — lookup provides the partition key (RecordId + CreatedAt) needed
        // to locate the PR record in the time-partitioned store. Both rows are written together on
        // first webhook arrival, so divergence (lookup exists, PR row missing) indicates a data
        // integrity failure rather than a legitimately missing PR.
        var lookup = await lookups.FindAsync(organizationId, repositoryId, pullRequestNumber, ct);
        if (lookup is null)
        {
            LogCheckRunBypassPrNotFound(logger, organizationId, repositoryId, pullRequestNumber);
            return CheckRunBypassOutcome.PrNotFound;
        }

        var pr = await pullRequests.FindAsync(
            lookup.PullRequestRecordId,
            lookup.PullRequestCreatedAtUtc,
            ct
        );
        if (pr is null)
        {
            LogCheckRunBypassPrNotFound(logger, organizationId, repositoryId, pullRequestNumber);
            return CheckRunBypassOutcome.PrNotFound;
        }

        var state = pr.CheckRunState;
        if (state is null)
        {
            // The newest record may have been created after a stacked-branch collapse and
            // lack the check-run state. Search older records for the same PR number.
            var candidates = await pullRequests.FindByNumberAsync(
                organizationId,
                pullRequestNumber,
                ct
            );
            foreach (var candidate in candidates)
            {
                if (candidate.CheckRunState is { } stored)
                {
                    state = stored;
                    pr = candidate;
                    break;
                }
            }
        }

        if (state is null)
        {
            LogCheckRunBypassNotFound(
                logger,
                organizationId,
                repository.OwnerQualifiedName,
                pullRequestNumber
            );
            return CheckRunBypassOutcome.NotFound;
        }

        if (state.State == CheckRunBlockState.Removed)
        {
            LogCheckRunBypassAlreadyRemoved(
                logger,
                organizationId,
                repository.OwnerQualifiedName,
                pullRequestNumber,
                state.CheckRunId
            );
            return CheckRunBypassOutcome.Cleared;
        }

        var write = new CheckRunWrite(
            Name: CheckRunConstants.ZeeqCheckRunName,
            HeadSha: pr.HeadSha,
            Status: CheckRunStatusKind.Completed,
            Conclusion: CheckRunConclusionKind.Success,
            Title: "Zeeq review block cleared",
            Summary: $"The Zeeq code review block was cleared by {removedBy}.",
            DetailsUrl: null,
            IncludeBypassAction: false
        );

        try
        {
            await client.UpdateAsync(
                organizationId,
                repository.OwnerQualifiedName,
                state.CheckRunId,
                write,
                ct
            );

            state.State = CheckRunBlockState.Removed;
            state.RemovedBy = removedBy;
            state.RemovedAtUtc = DateTimeOffset.UtcNow;

            await pullRequests.UpsertAsync(pr, ct);

            LogCheckRunBypassed(
                logger,
                organizationId,
                repository.OwnerQualifiedName,
                pullRequestNumber,
                state.CheckRunId,
                removedBy
            );

            activity?.AddEvent(
                [
                    ("github.check_run.id", state.CheckRunId),
                    ("github.check_run.conclusion", "success"),
                ],
                "code_review.check_run.bypass_completed"
            );

            return CheckRunBypassOutcome.Cleared;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckRunBypassFailed(
                logger,
                ex,
                organizationId,
                repository.OwnerQualifiedName,
                pullRequestNumber,
                state.CheckRunId
            );
            return CheckRunBypassOutcome.Failed;
        }
    }

    private static (
        CheckRunConclusionKind? Conclusion,
        CheckRunBlockState State,
        string Title,
        string Summary
    ) DetermineResolution(CodeReviewRecord review, CodeRepositoryReviewCheckRunConfiguration config)
    {
        if (review.Status != CodeReviewStatus.Completed)
        {
            return (
                CheckRunConclusionKind.Neutral,
                CheckRunBlockState.Removed,
                "Zeeq code review errored",
                "The review could not be completed."
            );
        }

        var totalFindings =
            review.CriticalFindings
            + review.MajorFindings
            + review.MinorFindings
            + review.SuggestionFindings
            + review.CommentFindings;
        if (totalFindings == 0)
        {
            return (
                CheckRunConclusionKind.Neutral,
                CheckRunBlockState.Removed,
                "Zeeq code review complete",
                "No reviewable findings were produced for this commit."
            );
        }

        if (config.ShouldBlock(review.CriticalFindings, review.MajorFindings))
        {
            var findings = GetFindingsSummary(review);
            return (
                CheckRunConclusionKind.ActionRequired,
                CheckRunBlockState.Blocking,
                "Zeeq code review found blocking findings",
                $"""
                This review found findings that meet the repository's blocking threshold:

                {findings}

                The merge is blocked until the findings are addressed or the check is bypassed.
                """
            );
        }

        return (
            CheckRunConclusionKind.Success,
            CheckRunBlockState.Removed,
            "Zeeq code review passed",
            "No findings met the repository's blocking threshold."
        );
    }

    private static string GetFindingsSummary(CodeReviewRecord review)
    {
        var parts = new[]
        {
            review.CriticalFindings > 0 ? $"- {review.CriticalFindings} critical" : null,
            review.MajorFindings > 0 ? $"- {review.MajorFindings} major" : null,
            review.MinorFindings > 0 ? $"- {review.MinorFindings} minor" : null,
        };

        return string.Join("\n", parts.Where(p => p is not null));
    }

    private string BuildPrViewLink(PullRequestRecord pr)
    {
        var token = CodeReviewSingleViewToken.Encode(pr.CreatedAtUtc, CodeReviewSingleViewMode.Pr);
        return linkFactory.BuildPublicAssetLink(
            $"code-reviews/pull-requests/{pr.Id}/single?c={Uri.EscapeDataString(token)}"
        );
    }

    [LoggerMessage(
        EventId = 3300,
        Level = LogLevel.Information,
        Message = "Posted pending check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, HeadSha={HeadSha}, CheckRunId={CheckRunId}"
    )]
    private static partial void LogCheckRunPendingPosted(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string headSha,
        long checkRunId
    );

    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Warning,
        Message = "Failed to post pending check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, HeadSha={HeadSha}"
    )]
    private static partial void LogCheckRunPendingFailed(
        ILogger logger,
        Exception ex,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string headSha
    );

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Information,
        Message = "Resolved check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, HeadSha={HeadSha}, Conclusion={Conclusion}, Blocking={Blocking}"
    )]
    private static partial void LogCheckRunResolved(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string headSha,
        string? conclusion,
        bool blocking
    );

    [LoggerMessage(
        EventId = 3303,
        Level = LogLevel.Warning,
        Message = "Failed to resolve check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, HeadSha={HeadSha}"
    )]
    private static partial void LogCheckRunResolveFailed(
        ILogger logger,
        Exception ex,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string headSha
    );

    [LoggerMessage(
        EventId = 3304,
        Level = LogLevel.Information,
        Message = "Cleared blocking check run by bypass. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CheckRunId={CheckRunId}, RemovedBy={RemovedBy}"
    )]
    private static partial void LogCheckRunBypassed(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        long checkRunId,
        string removedBy
    );

    [LoggerMessage(
        EventId = 3305,
        Level = LogLevel.Information,
        Message = "Bypass attempted but no check-run state exists. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}"
    )]
    private static partial void LogCheckRunBypassNotFound(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber
    );

    [LoggerMessage(
        EventId = 3306,
        Level = LogLevel.Information,
        Message = "Bypass attempted but check was already removed. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CheckRunId={CheckRunId}"
    )]
    private static partial void LogCheckRunBypassAlreadyRemoved(
        ILogger logger,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        long checkRunId
    );

    [LoggerMessage(
        EventId = 3307,
        Level = LogLevel.Warning,
        Message = "Failed to clear blocking check run. OrganizationId={OrganizationId}, Repo={OwnerQualifiedRepoName}, PullRequestNumber={PullRequestNumber}, CheckRunId={CheckRunId}"
    )]
    private static partial void LogCheckRunBypassFailed(
        ILogger logger,
        Exception ex,
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        long checkRunId
    );

    [LoggerMessage(
        EventId = 3308,
        Level = LogLevel.Information,
        Message = "Bypass attempted but PR was not found. OrganizationId={OrganizationId}, RepositoryId={RepositoryId}, PullRequestNumber={PullRequestNumber}"
    )]
    private static partial void LogCheckRunBypassPrNotFound(
        ILogger logger,
        string organizationId,
        string repositoryId,
        int pullRequestNumber
    );
}
