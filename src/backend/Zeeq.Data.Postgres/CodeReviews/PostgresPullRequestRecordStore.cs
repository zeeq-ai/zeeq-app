using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for durable pull request stream records.
/// </summary>
/// <remarks>
/// Pull request rows are the UI-facing stream data created from GitHub webhook
/// events. They are partitioned by <c>CreatedAtUtc</c> for cheap recent reads
/// and long-term retention, while <see cref="PullRequestLookup" /> owns the
/// cross-partition uniqueness rule for provider PR identity.
/// </remarks>
internal sealed class PostgresPullRequestRecordStore(PostgresDbContext db) : IPullRequestRecordStore
{
    /// <summary>
    /// Creates or updates a partitioned pull request record.
    /// </summary>
    /// <remarks>
    /// The caller supplies both ID and <c>CreatedAtUtc</c>; together they form
    /// the primary key and partition boundary. GitHub webhooks update mutable
    /// PR metadata such as title, head SHA, labels, draft state, and claim state.
    /// </remarks>
    public async Task<PullRequestRecord> UpsertAsync(
        PullRequestRecord pullRequest,
        CancellationToken cancellationToken
    )
    {
        var existing = await FindAsync(pullRequest.Id, pullRequest.CreatedAtUtc, cancellationToken);

        if (existing is null)
        {
            db.PullRequestRecords.Add(pullRequest);
            await db.SaveChangesAsync(cancellationToken);
            return pullRequest;
        }

        // These fields mirror mutable GitHub state or Zeeq UI state. The
        // organization, repository ID, PR number, and partition timestamp stay fixed.
        existing.OwnerQualifiedRepoName = pullRequest.OwnerQualifiedRepoName;
        existing.GitHubNodeId = pullRequest.GitHubNodeId;
        existing.Title = pullRequest.Title;
        existing.Branch = pullRequest.Branch;
        existing.BaseBranch = pullRequest.BaseBranch;
        existing.HeadSha = pullRequest.HeadSha;
        existing.AuthorLogin = pullRequest.AuthorLogin;
        existing.HtmlUrl = pullRequest.HtmlUrl;
        existing.IsDraft = pullRequest.IsDraft;
        existing.State = pullRequest.State;
        existing.ClaimStatus = pullRequest.ClaimStatus;
        existing.ClaimedByUserId = pullRequest.ClaimedByUserId;
        existing.FeatureId = pullRequest.FeatureId;
        existing.TagsJson = pullRequest.TagsJson;
        existing.LabelsJson = pullRequest.LabelsJson;
        existing.LastWebhookAtUtc = pullRequest.LastWebhookAtUtc;
        existing.UpdatedAtUtc = pullRequest.UpdatedAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Finds one pull request record by stable ID and partition timestamp.
    /// </summary>
    /// <remarks>
    /// Detail views should use this path after resolving the timestamp from
    /// <see cref="PullRequestLookup" /> or a stream cursor.
    /// </remarks>
    public Task<PullRequestRecord?> FindAsync(
        string id,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken
    ) =>
        db
            .PullRequestRecords.TagWithOperationCallSite("pull_request_record.find")
            .FirstOrDefaultAsync(
                record => record.Id == id && record.CreatedAtUtc == createdAtUtc,
                cancellationToken
            );

    /// <inheritdoc />
    public Task<PullRequestRecord?> FindByHeadShaAsync(
        string organizationId,
        string repositoryId,
        string headSha,
        CancellationToken cancellationToken
    ) =>
        db
            .PullRequestRecords.TagWithOperationCallSite("pull_request_record.find_by_head_sha")
            .Where(record =>
                record.OrganizationId == organizationId
                && record.RepositoryId == repositoryId
                && record.HeadSha == headSha
            )
            .OrderByDescending(record => record.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PullRequestRecord?> FindByHeadShaWithCheckRunAsync(
        string organizationId,
        string repositoryId,
        string headSha,
        CancellationToken cancellationToken
    ) =>
        db.PullRequestRecords.TagWithOperationCallSite(
                "pull_request_record.find_by_head_sha_with_check_run"
            )
            .Where(record =>
                record.OrganizationId == organizationId
                && record.RepositoryId == repositoryId
                && record.HeadSha == headSha
                && record.CheckRunState != null
                && record.State == PullRequestState.Open
            )
            .OrderByDescending(record => record.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PullRequestRecord>> FindByNumberAsync(
        string organizationId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    )
    {
        return await db
            .PullRequestRecords.TagWithOperationCallSite("pull_request_record.find_by_number")
            .Where(record =>
                record.OrganizationId == organizationId
                && record.PullRequestNumber == pullRequestNumber
            )
            .OrderByDescending(record => record.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Lists recent pull requests for high-read stream views.
    /// </summary>
    /// <remarks>
    /// Results are newest first and use seek pagination by full timestamp plus
    /// row ID. This supports a user sitting on the page and refreshing from a
    /// known cursor without forcing offset scans across partitions.
    /// </remarks>
    public async Task<CodeReviewStreamPage<PullRequestRecord>> ListRecentAsync(
        PullRequestStreamQuery query,
        CancellationToken cancellationToken
    )
    {
        var rows = db
            .PullRequestRecords.TagWithOperationCallSite("pull_request_record.list_recent")
            .Where(record => record.OrganizationId == query.OrganizationId);

        if (query.TeamId is not null)
        {
            rows = rows.Where(record => record.TeamId == query.TeamId);
        }

        if (query.RepositoryId is not null)
        {
            rows = rows.Where(record => record.RepositoryId == query.RepositoryId);
        }

        if (query.ClaimStatus is not null)
        {
            rows = rows.Where(record => record.ClaimStatus == query.ClaimStatus);
        }

        if (query.SubjectUserId is not null)
        {
            rows = rows.Where(record => record.ClaimedByUserId == query.SubjectUserId);
        }

        if (query.Cursor is { } cursor)
        {
            // Seek pagination: continue strictly older than the last rendered row.
            rows = rows.Where(record =>
                record.CreatedAtUtc < cursor.CreatedAtUtc
                || (
                    record.CreatedAtUtc == cursor.CreatedAtUtc
                    && string.Compare(record.Id, cursor.Id) < 0
                )
            );
        }

        // Keep page sizes bounded at the store layer so the recent stream stays cheap.
        var items = await rows.OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => record.Id)
            .Take(Math.Clamp(query.PageSize, 1, 100))
            .ToArrayAsync(cancellationToken);

        return items.ToStreamPage();
    }
}
