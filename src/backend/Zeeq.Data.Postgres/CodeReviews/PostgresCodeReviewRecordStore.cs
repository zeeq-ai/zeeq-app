using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for durable code review execution records.
/// </summary>
/// <remarks>
/// Review records are partitioned by <c>CreatedAtUtc</c>, so every precise
/// lookup carries both the stable row ID and the creation timestamp. Stream
/// queries are optimized for the high-read recent-review list shown in the UI.
/// </remarks>
internal sealed class PostgresCodeReviewRecordStore(PostgresDbContext db) : ICodeReviewRecordStore
{
    /// <summary>
    /// Persists a new review execution record.
    /// </summary>
    /// <remarks>
    /// Review records are created as a new execution attempt rather than
    /// upserted. Later handlers update the tracked row state and result fields
    /// through the same partition key.
    /// </remarks>
    public async Task<CodeReviewRecord> AddAsync(
        CodeReviewRecord review,
        CancellationToken cancellationToken
    )
    {
        db.CodeReviewRecords.Add(review);
        await db.SaveChangesAsync(cancellationToken);
        return review;
    }

    /// <summary>
    /// Updates mutable execution state for an existing review record.
    /// </summary>
    /// <remarks>
    /// The incoming record carries the partition key. Loading first keeps EF
    /// pointed at the tracked row in the correct partition instead of attaching
    /// a detached object that may not include every persisted field.
    /// </remarks>
    public async Task<CodeReviewRecord> UpdateAsync(
        CodeReviewRecord review,
        CancellationToken cancellationToken
    )
    {
        var existing =
            await FindAsync(review.Id, review.CreatedAtUtc, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Code review record {review.Id} was not found."
            );

        ApplyExecutionUpdate(existing, review);

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Finds one review record by stable ID and partition timestamp.
    /// </summary>
    /// <remarks>
    /// The timestamp is part of the primary key and lets Postgres prune
    /// partitions for detail lookups.
    /// </remarks>
    public Task<CodeReviewRecord?> FindAsync(
        string id,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewRecords.TagWithOperationCallSite("code_review_record.find")
            .FirstOrDefaultAsync(
                review => review.Id == id && review.CreatedAtUtc == createdAtUtc,
                cancellationToken
            );

    /// <summary>
    /// Finds the latest review attempt for one partitioned pull request row.
    /// </summary>
    /// <remarks>
    /// Budget policy needs the newest terminal review before it accepts another
    /// attempt. This lookup stays on <c>PullRequestRecordId</c> because the
    /// pull-request workflow has already resolved the cross-partition lookup row.
    /// </remarks>
    public Task<CodeReviewRecord?> FindNewestForPullRequestAsync(
        string organizationId,
        string pullRequestRecordId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewRecords.TagWithOperationCallSite(
                "code_review_record.find_newest_for_pull_request"
            )
            .Where(review =>
                review.OrganizationId == organizationId
                && review.PullRequestRecordId == pullRequestRecordId
            )
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CodeReviewRecord?> FindNewestCompletedForPullRequestAsync(
        string organizationId,
        string pullRequestRecordId,
        DateTimeOffset pullRequestCreatedAtUtc,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewRecords.TagWithOperationCallSite(
                "code_review_record.find_newest_completed_for_pull_request"
            )
            .Where(review =>
                review.OrganizationId == organizationId
                && review.PullRequestRecordId == pullRequestRecordId
                && review.Status == CodeReviewStatus.Completed
                && review.CreatedAtUtc >= pullRequestCreatedAtUtc
            )
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CodeReviewRecord?> FindNewestCompletedForBranchAsync(
        string organizationId,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewRecords.TagWithOperationCallSite(
                "code_review_record.find_newest_completed_for_branch"
            )
            .Where(review =>
                review.OrganizationId == organizationId
                && review.RepositoryId == repositoryId
                && review.Branch == branch
                && review.Status == CodeReviewStatus.Completed
            )
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CodeReviewRecord?> FindNewestCompletedForReviewGroupAsync(
        string organizationId,
        string reviewGroupId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewRecords.TagWithOperationCallSite(
                "code_review_record.find_newest_completed_for_group"
            )
            .Where(review =>
                review.OrganizationId == organizationId
                && review.ReviewGroupId == reviewGroupId
                && review.Status == CodeReviewStatus.Completed
            )
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Lists recent review records for an organization stream.
    /// </summary>
    /// <remarks>
    /// Results are returned newest first using <c>CreatedAtUtc DESC, Id DESC</c>.
    /// The cursor uses the same two fields so repeated refreshes and infinite
    /// scroll requests do not skip or duplicate rows created at the same time.
    /// </remarks>
    public async Task<CodeReviewStreamPage<CodeReviewRecord>> ListRecentAsync(
        CodeReviewStreamQuery query,
        CancellationToken cancellationToken
    )
    {
        var rows = db
            .CodeReviewRecords.TagWithOperationCallSite("code_review_record.list_recent")
            .Where(review => review.OrganizationId == query.OrganizationId);

        if (query.TeamId is not null)
        {
            rows = rows.Where(review => review.TeamId == query.TeamId);
        }

        if (query.RepositoryId is not null)
        {
            rows = rows.Where(review => review.RepositoryId == query.RepositoryId);
        }

        if (query.Status is not null)
        {
            rows = rows.Where(review => review.Status == query.Status);
        }

        if (query.Cursor is { } cursor)
        {
            // Seek pagination: move strictly older than the last row from the prior page.
            rows = rows.Where(review =>
                review.CreatedAtUtc < cursor.CreatedAtUtc
                || (
                    review.CreatedAtUtc == cursor.CreatedAtUtc
                    && string.Compare(review.Id, cursor.Id) < 0
                )
            );
        }

        // Bound page size at the store boundary so callers cannot request an
        // unbounded recent stream from the partitioned table.
        var items = await rows.OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .Take(Math.Clamp(query.PageSize, 1, 100))
            .ToArrayAsync(cancellationToken);

        return items.ToStreamPage();
    }

    /// <summary>
    /// Lists review history for one selected pull request.
    /// </summary>
    /// <remarks>
    /// The pull request creation timestamp is not a foreign key column on the
    /// review row. It is still valuable here because a review cannot predate its
    /// PR, so the query uses it as the lower partition boundary before seeking by
    /// review <c>CreatedAtUtc DESC, Id DESC</c>.
    /// </remarks>
    public async Task<CodeReviewStreamPage<CodeReviewRecord>> ListForPullRequestAsync(
        PullRequestReviewStreamQuery query,
        CancellationToken cancellationToken
    )
    {
        var rows = db
            .CodeReviewRecords.TagWithOperationCallSite("code_review_record.list_for_pull_request")
            .Where(review =>
                review.OrganizationId == query.OrganizationId
                && review.PullRequestRecordId == query.PullRequestRecordId
                && review.CreatedAtUtc >= query.PullRequestCreatedAtUtc
            );

        if (query.Cursor is { } cursor)
        {
            // Seek pagination: move strictly older than the last review from the prior page.
            rows = rows.Where(review =>
                review.CreatedAtUtc < cursor.CreatedAtUtc
                || (
                    review.CreatedAtUtc == cursor.CreatedAtUtc
                    && string.Compare(review.Id, cursor.Id) < 0
                )
            );
        }

        var items = await rows.OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .Take(Math.Clamp(query.PageSize, 1, 100))
            .ToArrayAsync(cancellationToken);

        return items.ToStreamPage();
    }

    /// <summary>
    /// Lists minimal review updates for inbox polling.
    /// </summary>
    /// <remarks>
    /// The update stream sorts by mutable <c>UpdatedAtUtc</c> because that is the
    /// UI's high-water mark. It also requires a review-created lower bound for
    /// partition pruning. The join to <see cref="PullRequestLookup"/> is
    /// deliberate: that non-partitioned table already stores the current
    /// <c>PullRequestCreatedAtUtc</c> needed by the frontend cursor model, so the
    /// review row does not need to duplicate it.
    /// </remarks>
    public async Task<CodeReviewUpdateStreamPage> ListInboxUpdatesAsync(
        CodeReviewUpdateStreamQuery query,
        CancellationToken cancellationToken
    )
    {
        ValidateUpdateCursorFilterShape(query);

        var lowerBound =
            query.Cursor?.ReviewCreatedAtLowerBoundUtc ?? query.ReviewCreatedAtLowerBoundUtc;
        if (lowerBound is null)
        {
            return new([], null, null);
        }
        var effectiveQuery = query with { ReviewCreatedAtLowerBoundUtc = lowerBound };

        var rows = db
            .CodeReviewRecords.TagWithOperationCallSite("code_review_record.list_inbox_updates")
            .Where(review =>
                review.OrganizationId == query.OrganizationId
                && review.CreatedAtUtc >= lowerBound.Value
            );

        if (query.TeamId is not null)
        {
            rows = rows.Where(review => review.TeamId == query.TeamId);
        }

        if (query.RepositoryId is not null)
        {
            rows = rows.Where(review => review.RepositoryId == query.RepositoryId);
        }

        if (query.Cursor is { } cursor)
        {
            // Polling pagination: continue strictly after the last update observed.
            rows = rows.Where(review =>
                review.UpdatedAtUtc > cursor.UpdatedAtUtc
                || (
                    review.UpdatedAtUtc == cursor.UpdatedAtUtc
                    && review.CreatedAtUtc > cursor.CreatedAtUtc
                )
                || (
                    review.UpdatedAtUtc == cursor.UpdatedAtUtc
                    && review.CreatedAtUtc == cursor.CreatedAtUtc
                    && string.Compare(review.Id, cursor.Id) > 0
                )
            );
        }

        var joined = rows.Join(
            db.PullRequestLookups.TagWithOperationCallSite(
                "code_review_record.list_inbox_updates.pull_request_lookup"
            ),
            // Agent-origin reviews carry a null PullRequestRecordId and never match a
            // lookup row, so the inner join excludes them and the key is non-null here.
            review => new
            {
                review.OrganizationId,
                PullRequestRecordId = review.PullRequestRecordId!,
            },
            lookup => new { lookup.OrganizationId, lookup.PullRequestRecordId },
            (review, lookup) => new { Review = review, Lookup = lookup }
        );

        if (query.Scope == CodeReviewInboxScope.Mine)
        {
            // TODO(code-review-account-mapping): Extend this "Mine" scope to
            // include provider-authored PRs once Zeeq user/account mappings are
            // available. For this slice, the existing first-party relationship is
            // the claimed PR user id stored on PullRequestRecord.
            joined = joined
                .Join(
                    db.PullRequestRecords.TagWithOperationCallSite(
                        "code_review_record.list_inbox_updates.mine_pull_request"
                    ),
                    row => new
                    {
                        row.Lookup.OrganizationId,
                        Id = row.Lookup.PullRequestRecordId,
                        CreatedAtUtc = row.Lookup.PullRequestCreatedAtUtc,
                    },
                    pullRequest => new
                    {
                        pullRequest.OrganizationId,
                        pullRequest.Id,
                        pullRequest.CreatedAtUtc,
                    },
                    (row, pullRequest) =>
                        new
                        {
                            row.Review,
                            row.Lookup,
                            pullRequest.ClaimedByUserId,
                        }
                )
                .Where(row => row.ClaimedByUserId == query.SubjectUserId)
                .Select(row => new { row.Review, row.Lookup });
        }

        var updates = await joined
            .OrderBy(row => row.Review.UpdatedAtUtc)
            .ThenBy(row => row.Review.CreatedAtUtc)
            .ThenBy(row => row.Review.Id)
            .Take(Math.Clamp(query.PageSize, 1, 100))
            .Select(row => new CodeReviewInboxUpdate(
                row.Review.PullRequestRecordId!,
                row.Lookup.PullRequestCreatedAtUtc,
                row.Review.Id,
                row.Review.CreatedAtUtc,
                row.Review.Status,
                row.Review.CriticalFindings,
                row.Review.MajorFindings,
                row.Review.MinorFindings,
                row.Review.SuggestionFindings,
                row.Review.CommentFindings,
                row.Review.RemainingReviewBudget,
                row.Review.UpdatedAtUtc
            ))
            .ToArrayAsync(cancellationToken);

        return updates.ToUpdateStreamPage(query.Cursor, effectiveQuery);
    }

    private static void ValidateUpdateCursorFilterShape(CodeReviewUpdateStreamQuery query)
    {
        if (
            query.Scope == CodeReviewInboxScope.Mine
            && string.IsNullOrWhiteSpace(query.SubjectUserId)
        )
        {
            throw new ArgumentException(
                "Mine-scoped inbox update queries require a server-derived subject user id.",
                nameof(query)
            );
        }

        if (query.Cursor is not { } cursor)
        {
            return;
        }

        var filterMatches =
            string.Equals(cursor.TeamId, query.TeamId, StringComparison.Ordinal)
            && string.Equals(cursor.RepositoryId, query.RepositoryId, StringComparison.Ordinal)
            && cursor.Scope == query.Scope
            && string.Equals(cursor.SubjectUserId, query.SubjectUserId, StringComparison.Ordinal);

        if (!filterMatches)
        {
            throw new ArgumentException(
                "Inbox update cursors cannot be reused with a different team, repository, or user scope.",
                nameof(query)
            );
        }
    }

    /// <summary>
    /// Lists agent-origin reviews for the single-review view related set, newest first,
    /// matching by session id and/or group id (either). Returns empty when both keys are null.
    /// </summary>
    public async Task<IReadOnlyList<CodeReviewRecord>> ListForAgentAsync(
        string organizationId,
        string? agentSessionId,
        string? reviewGroupId,
        int maxRecords,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(agentSessionId) && string.IsNullOrEmpty(reviewGroupId))
        {
            return [];
        }

        var query = db
            .CodeReviewRecords.TagWithOperationCallSite("code_review_record.list_for_agent")
            .AsNoTracking()
            .Where(r => r.OrganizationId == organizationId);

        // Either-key rule: match when session id OR group id matches (or both).
        // NOTE: (review finding — no explicit `r.AgentSessionId != null` guard needed) The
        // parameter null-checks short-circuit unused branches; on the row side SQL evaluates
        // `null = @param` to unknown, so records whose key column is null are already excluded.
        query = query.Where(r =>
            (agentSessionId != null && r.AgentSessionId == agentSessionId)
            || (reviewGroupId != null && r.ReviewGroupId == reviewGroupId)
        );

        return await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(Math.Clamp(maxRecords, 1, 100))
            .ToArrayAsync(cancellationToken);
    }

    private static void ApplyExecutionUpdate(CodeReviewRecord target, CodeReviewRecord source)
    {
        // Keep this explicit instead of using a generic mapper: review rows are
        // partitioned, and identity/partition fields must not be copied onto the
        // tracked EF entity during execution-state updates.
        target.OwnerQualifiedRepoName = source.OwnerQualifiedRepoName;
        target.Branch = source.Branch;
        target.Title = source.Title;
        target.AuthorLogin = source.AuthorLogin;
        target.Status = source.Status;
        target.RequestOrigin = source.RequestOrigin;
        target.ReviewGroupId = source.ReviewGroupId;
        target.PreviousReviewId = source.PreviousReviewId;
        target.ExecutionTraceParent = source.ExecutionTraceParent;
        target.ExecutionTraceState = source.ExecutionTraceState;
        target.RemainingReviewBudget = source.RemainingReviewBudget;
        target.CriticalFindings = source.CriticalFindings;
        target.MajorFindings = source.MajorFindings;
        target.MinorFindings = source.MinorFindings;
        target.SuggestionFindings = source.SuggestionFindings;
        target.CommentFindings = source.CommentFindings;
        target.SourceTelemetryPayload = source.SourceTelemetryPayload;
        target.FindingsStorageUri = source.FindingsStorageUri;
        target.FailureMessage = source.FailureMessage;
        target.UpdatedAtUtc = source.UpdatedAtUtc;
    }
}
