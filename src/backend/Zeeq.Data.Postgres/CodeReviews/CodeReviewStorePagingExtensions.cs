using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Converts newest-first store query results into shared stream page contracts.
/// </summary>
/// <remarks>
/// Pull request and code review stream queries both page by the same boundary:
/// full <c>CreatedAtUtc</c> timestamp plus stable row ID. That boundary matches
/// the partition key and keeps pagination portable across Postgres and any
/// future store implementation. This helper assumes the caller already applied
/// filters, ordering, and page-size limits in the database.
/// </remarks>
internal static class CodeReviewStorePagingExtensions
{
    /// <summary>
    /// Builds a stream page for pull request rows.
    /// </summary>
    /// <remarks>
    /// Callers must pass rows ordered by <c>CreatedAtUtc DESC, Id DESC</c>.
    /// The first row becomes the newest cursor, and the last row becomes the
    /// next-page cursor. Empty result sets return null cursors so callers can
    /// distinguish "no boundary" from "continue after this row".
    /// </remarks>
    public static CodeReviewStreamPage<PullRequestRecord> ToStreamPage(
        this IReadOnlyList<PullRequestRecord> items
    ) =>
        new(
            items,
            // Infinite-scroll requests continue with rows strictly older than the last row.
            NextCursor: ToCursor(items.LastOrDefault()),
            // Refresh requests can ask for rows newer than the first row already rendered.
            NewestCursor: ToCursor(items.FirstOrDefault())
        );

    /// <summary>
    /// Builds a stream page for code review rows.
    /// </summary>
    /// <remarks>
    /// The cursor shape is intentionally identical to pull requests so the UI
    /// and API can use one pagination model for both streams. The timestamp is
    /// the partition discriminator; the ID is the deterministic tie-breaker for
    /// rows created at the same instant.
    /// </remarks>
    public static CodeReviewStreamPage<CodeReviewRecord> ToStreamPage(
        this IReadOnlyList<CodeReviewRecord> items
    ) =>
        new(
            items,
            // Infinite-scroll requests continue with rows strictly older than the last row.
            NextCursor: ToCursor(items.LastOrDefault()),
            // Refresh requests can ask for rows newer than the first row already rendered.
            NewestCursor: ToCursor(items.FirstOrDefault())
        );

    /// <summary>
    /// Converts a pull request row into the shared stream cursor shape.
    /// </summary>
    /// <remarks>
    /// The cursor carries the full timestamp instead of a date bucket so detail
    /// lookups and page boundaries use the exact partition key stored on the row.
    /// </remarks>
    private static CodeReviewStreamCursor? ToCursor(PullRequestRecord? record) =>
        record is null ? null : new(record.CreatedAtUtc, record.Id);

    /// <summary>
    /// Converts a code review row into the shared stream cursor shape.
    /// </summary>
    /// <remarks>
    /// Code review streams use the same boundary as pull request streams so
    /// API handlers can share pagination code without knowing the row type.
    /// </remarks>
    private static CodeReviewStreamCursor? ToCursor(CodeReviewRecord? record) =>
        record is null ? null : new(record.CreatedAtUtc, record.Id);

    /// <summary>
    /// Builds an ascending update page for inbox polling.
    /// </summary>
    /// <remarks>
    /// Callers must pass rows ordered by
    /// <c>UpdatedAtUtc ASC, CodeReviewCreatedAtUtc ASC, CodeReviewRecordId ASC</c>.
    /// Empty result sets retain the caller's cursor when present so polling can
    /// safely retry from the same high-water mark.
    /// </remarks>
    public static CodeReviewUpdateStreamPage ToUpdateStreamPage(
        this IReadOnlyList<CodeReviewInboxUpdate> items,
        CodeReviewUpdateCursor? fallbackCursor,
        CodeReviewUpdateStreamQuery query
    )
    {
        var nextCursor = ToUpdateCursor(items.LastOrDefault(), fallbackCursor, query);

        return new(
            items,
            NextCursor: nextCursor,
            NewestCursor: ToUpdateCursor(items.LastOrDefault(), fallbackCursor, query)
        );
    }

    private static CodeReviewUpdateCursor? ToUpdateCursor(
        CodeReviewInboxUpdate? update,
        CodeReviewUpdateCursor? fallbackCursor,
        CodeReviewUpdateStreamQuery query
    )
    {
        if (update is not null)
        {
            return new(
                fallbackCursor?.ReviewCreatedAtLowerBoundUtc
                    ?? query.ReviewCreatedAtLowerBoundUtc
                    ?? update.CodeReviewCreatedAtUtc,
                update.UpdatedAtUtc,
                update.CodeReviewCreatedAtUtc,
                update.CodeReviewRecordId,
                query.TeamId,
                query.RepositoryId,
                query.Scope,
                query.SubjectUserId
            );
        }

        if (fallbackCursor is not null)
        {
            return fallbackCursor;
        }

        return query.ReviewCreatedAtLowerBoundUtc is null
            ? null
            : new(
                query.ReviewCreatedAtLowerBoundUtc.Value,
                DateTimeOffset.UtcNow,
                DateTimeOffset.MinValue,
                CodeReviewUpdateCursor.SyntheticHighWaterId,
                query.TeamId,
                query.RepositoryId,
                query.Scope,
                query.SubjectUserId
            );
    }
}
