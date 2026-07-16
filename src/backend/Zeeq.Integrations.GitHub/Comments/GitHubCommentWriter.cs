using System.Diagnostics;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.Extensions.Logging;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Creates or updates Zeeq-owned GitHub comments with rendered Markdown.
/// </summary>
/// <remarks>
/// The writer deliberately repeats the marker-scan fallback before creating a
/// new comment. A resolver result can become stale between read and write if a
/// comment was deleted or an anchor was missing; scanning before create keeps us
/// from duplicating Zeeq comments when GitHub still has the target marker.
/// </remarks>
internal sealed partial class GitHubCommentWriter(ILogger<GitHubCommentWriter> logger)
    : IGitHubCommentWriter
{
    /// <summary>
    /// Updates an existing target comment or creates the first one.
    /// </summary>
    public async Task<long> UpsertAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        long? existingCommentId,
        string body,
        CancellationToken cancellationToken
    )
    {
        var targetKey = target.ToStorageKey();
        LogWriterUpsertStarted(
            logger,
            targetKey,
            target.Kind,
            ownerQualifiedRepoName,
            existingCommentId,
            body.Length
        );

        if (existingCommentId is not null)
        {
            LogWriterDirectUpdateStarting(
                logger,
                targetKey,
                target.Kind,
                ownerQualifiedRepoName,
                existingCommentId.Value
            );
            var updated = await TryUpdateAsync(
                client,
                target.Kind,
                ownerQualifiedRepoName,
                existingCommentId.Value,
                body,
                cancellationToken
            );

            if (updated is not null)
            {
                LogWriterDirectUpdateCompleted(logger, targetKey, target.Kind, updated.Value);
                return updated.Value;
            }

            LogWriterDirectUpdateCommentMissing(
                logger,
                targetKey,
                target.Kind,
                existingCommentId.Value
            );
        }

        LogWriterMarkerScanStarting(logger, targetKey, target.Kind, ownerQualifiedRepoName);
        var scannedCount = 0;
        await foreach (
            var comment in EnumerateCandidatesAsync(
                client,
                target,
                ownerQualifiedRepoName,
                cancellationToken
            )
        )
        {
            scannedCount++;
            if (!GitHubCommentDomParser.ContainsRootForTarget(target, comment.Body))
            {
                continue;
            }

            LogWriterMarkerMatchFound(
                logger,
                targetKey,
                target.Kind,
                comment.CommentId,
                scannedCount
            );
            var startedAt = Stopwatch.GetTimestamp();
            var updated = await UpdateAsync(
                client,
                target.Kind,
                ownerQualifiedRepoName,
                comment.CommentId,
                body,
                cancellationToken
            );
            LogWriterMarkerUpdateCompleted(
                logger,
                targetKey,
                target.Kind,
                updated,
                Stopwatch.GetElapsedTime(startedAt)
            );

            return updated;
        }

        LogWriterMarkerScanCompletedWithoutMatch(logger, targetKey, target.Kind, scannedCount);
        LogWriterCreateStarting(logger, targetKey, target.Kind, ownerQualifiedRepoName);
        var createStartedAt = Stopwatch.GetTimestamp();
        var created = await CreateAsync(
            client,
            target,
            ownerQualifiedRepoName,
            body,
            cancellationToken
        );
        LogWriterCreateCompleted(
            logger,
            targetKey,
            target.Kind,
            created,
            Stopwatch.GetElapsedTime(createStartedAt)
        );

        return created;
    }

    private async Task<long?> TryUpdateAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetKind kind,
        string ownerQualifiedRepoName,
        long commentId,
        string body,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var startedAt = Stopwatch.GetTimestamp();
            var updated = await UpdateAsync(
                client,
                kind,
                ownerQualifiedRepoName,
                commentId,
                body,
                cancellationToken
            );
            LogWriterUpdateCompleted(
                logger,
                kind,
                commentId,
                updated,
                Stopwatch.GetElapsedTime(startedAt)
            );

            return updated;
        }
        catch (GitHubCommentNotFoundException)
        {
            // The anchor was stale. Fall through to marker scan before create.
            return null;
        }
    }

    private static Task<long> UpdateAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetKind kind,
        string ownerQualifiedRepoName,
        long commentId,
        string body,
        CancellationToken cancellationToken
    ) =>
        IsIssueCommentTarget(kind)
            ? client.UpdateIssueCommentAsync(
                ownerQualifiedRepoName,
                commentId,
                body,
                cancellationToken
            )
            : client.UpdatePullRequestReviewCommentAsync(
                ownerQualifiedRepoName,
                commentId,
                body,
                cancellationToken
            );

    private static IAsyncEnumerable<GitHubCommentCandidate> EnumerateCandidatesAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        CancellationToken cancellationToken
    ) =>
        IsIssueCommentTarget(target.Kind)
            ? client.EnumerateIssueCommentsAsync(
                ownerQualifiedRepoName,
                target.PullRequestNumber,
                cancellationToken
            )
            : client.EnumeratePullRequestReviewCommentsAsync(
                ownerQualifiedRepoName,
                target.PullRequestNumber,
                cancellationToken
            );

    private static Task<long> CreateAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        string body,
        CancellationToken cancellationToken
    )
    {
        if (IsIssueCommentTarget(target.Kind))
        {
            return client.CreateIssueCommentAsync(
                ownerQualifiedRepoName,
                target.PullRequestNumber,
                body,
                cancellationToken
            );
        }

        if (long.TryParse(target.ScopeKey, out var parentCommentId))
        {
            return client.CreatePullRequestReviewReplyAsync(
                ownerQualifiedRepoName,
                target.PullRequestNumber,
                parentCommentId,
                body,
                cancellationToken
            );
        }

        throw new InvalidOperationException(
            "Review-thread comment creation requires ScopeKey to contain the parent GitHub review comment id."
        );
    }

    private static bool IsIssueCommentTarget(GitHubCommentTargetKind kind) =>
        kind
            is GitHubCommentTargetKind.PullRequestSummary
                or GitHubCommentTargetKind.StandaloneIssueComment;

    [LoggerMessage(
        EventId = 3600,
        Level = LogLevel.Information,
        Message = "Started GitHub comment writer upsert. Target={TargetKey}, Kind={Kind}, Repo={OwnerQualifiedRepoName}, ExistingCommentId={ExistingCommentId}, BodyCharCount={BodyCharCount}"
    )]
    private static partial void LogWriterUpsertStarted(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        string ownerQualifiedRepoName,
        long? existingCommentId,
        int bodyCharCount
    );

    [LoggerMessage(
        EventId = 3601,
        Level = LogLevel.Information,
        Message = "Starting direct GitHub comment update. Target={TargetKey}, Kind={Kind}, Repo={OwnerQualifiedRepoName}, CommentId={CommentId}"
    )]
    private static partial void LogWriterDirectUpdateStarting(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        string ownerQualifiedRepoName,
        long commentId
    );

    [LoggerMessage(
        EventId = 3602,
        Level = LogLevel.Information,
        Message = "Completed direct GitHub comment update. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}"
    )]
    private static partial void LogWriterDirectUpdateCompleted(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        long commentId
    );

    [LoggerMessage(
        EventId = 3603,
        Level = LogLevel.Warning,
        Message = "Direct GitHub comment update target was missing. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}"
    )]
    private static partial void LogWriterDirectUpdateCommentMissing(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        long commentId
    );

    [LoggerMessage(
        EventId = 3604,
        Level = LogLevel.Information,
        Message = "Starting GitHub comment marker scan before create. Target={TargetKey}, Kind={Kind}, Repo={OwnerQualifiedRepoName}"
    )]
    private static partial void LogWriterMarkerScanStarting(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        string ownerQualifiedRepoName
    );

    [LoggerMessage(
        EventId = 3605,
        Level = LogLevel.Information,
        Message = "Found existing GitHub comment by marker scan. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}, ScannedCount={ScannedCount}"
    )]
    private static partial void LogWriterMarkerMatchFound(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        long commentId,
        int scannedCount
    );

    [LoggerMessage(
        EventId = 3606,
        Level = LogLevel.Information,
        Message = "Completed marker-scan GitHub comment update. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}, Duration={Duration}"
    )]
    private static partial void LogWriterMarkerUpdateCompleted(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        long commentId,
        TimeSpan duration
    );

    [LoggerMessage(
        EventId = 3607,
        Level = LogLevel.Information,
        Message = "Completed GitHub comment marker scan without match. Target={TargetKey}, Kind={Kind}, ScannedCount={ScannedCount}"
    )]
    private static partial void LogWriterMarkerScanCompletedWithoutMatch(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        int scannedCount
    );

    [LoggerMessage(
        EventId = 3608,
        Level = LogLevel.Information,
        Message = "Starting GitHub comment create. Target={TargetKey}, Kind={Kind}, Repo={OwnerQualifiedRepoName}"
    )]
    private static partial void LogWriterCreateStarting(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        string ownerQualifiedRepoName
    );

    [LoggerMessage(
        EventId = 3609,
        Level = LogLevel.Information,
        Message = "Completed GitHub comment create. Target={TargetKey}, Kind={Kind}, CommentId={CommentId}, Duration={Duration}"
    )]
    private static partial void LogWriterCreateCompleted(
        ILogger logger,
        string targetKey,
        GitHubCommentTargetKind kind,
        long commentId,
        TimeSpan duration
    );

    [LoggerMessage(
        EventId = 3610,
        Level = LogLevel.Information,
        Message = "Completed GitHub comment update call. Kind={Kind}, RequestedCommentId={RequestedCommentId}, ReturnedCommentId={ReturnedCommentId}, Duration={Duration}"
    )]
    private static partial void LogWriterUpdateCompleted(
        ILogger logger,
        GitHubCommentTargetKind kind,
        long requestedCommentId,
        long returnedCommentId,
        TimeSpan duration
    );
}
