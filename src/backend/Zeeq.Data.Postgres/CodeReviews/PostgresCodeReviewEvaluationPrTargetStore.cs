using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Read-only projection of known pull requests for the evaluation target picker.
/// </summary>
/// <remarks>
/// Browsing never creates rows. Selection reuses existing pull request records so the picker
/// reflects the same population the inbox already knows about, across every page.
/// </remarks>
internal sealed class PostgresCodeReviewEvaluationPrTargetStore(PostgresDbContext db)
    : ICodeReviewEvaluationPrTargetStore
{
    /// <inheritdoc />
    public async Task<CodeReviewEvaluationPrTargetPage> SearchAsync(
        CodeReviewEvaluationPrTargetQuery query,
        CancellationToken cancellationToken
    )
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var rows = db
            .PullRequestRecords.TagWithOperationCallSite("code_review_evaluation_pr_target.search")
            .Where(record =>
                record.OrganizationId == query.OrganizationId
                && record.RepositoryId == query.RepositoryId
            );

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            var term = query.Query.Trim();
            var pattern = $"%{term}%";
            var parsedNumber = ParseNumber(term);

            rows = rows.Where(record =>
                EF.Functions.ILike(record.Title, pattern)
                || (parsedNumber != null && record.PullRequestNumber == parsedNumber)
            );
        }

        if (
            query.CursorCreatedAtUtc is { } cursorCreatedAt
            && !string.IsNullOrWhiteSpace(query.CursorId)
        )
        {
            var cursorId = query.CursorId;
            rows = rows.Where(record =>
                record.CreatedAtUtc < cursorCreatedAt
                || (
                    record.CreatedAtUtc == cursorCreatedAt
                    && string.Compare(record.Id, cursorId) < 0
                )
            );
        }

        var items = await rows.OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => record.Id)
            .Take(pageSize + 1)
            .Select(record => new CodeReviewEvaluationPrTargetRecord(
                record.Id,
                record.CreatedAtUtc,
                record.PullRequestNumber,
                record.Title,
                record.State,
                record.HeadSha,
                record.AuthorLogin,
                record.UpdatedAtUtc
            ))
            .ToArrayAsync(cancellationToken);

        var hasMore = items.Length > pageSize;
        var page = hasMore ? items[..pageSize] : items;

        return new(
            page,
            hasMore ? page[^1].CreatedAtUtc : null,
            hasMore ? page[^1].PullRequestRecordId : null
        );
    }

    /// <inheritdoc />
    public async Task<CodeReviewEvaluationPrTargetRecord?> FindAsync(
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    ) =>
        await db
            .PullRequestRecords.TagWithOperationCallSite("code_review_evaluation_pr_target.find")
            .Where(record =>
                record.OrganizationId == organizationId
                && record.RepositoryId == repositoryId
                && record.PullRequestNumber == pullRequestNumber
            )
            .OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => record.Id)
            .Select(record => new CodeReviewEvaluationPrTargetRecord(
                record.Id,
                record.CreatedAtUtc,
                record.PullRequestNumber,
                record.Title,
                record.State,
                record.HeadSha,
                record.AuthorLogin,
                record.UpdatedAtUtc
            ))
            .FirstOrDefaultAsync(cancellationToken);

    private static int? ParseNumber(string term)
    {
        var trimmed = term.TrimStart('#');
        return int.TryParse(trimmed, out var number) && number > 0 ? number : null;
    }
}
