using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Metrics;

/// <summary>
/// Postgres read store for the metrics dashboard.
/// </summary>
/// <remarks>
/// Every query is single-organization, single-table, and window-scoped: the leading
/// <c>organization_id + metric_type + created_at_utc</c> predicate matches the partial indexes on
/// <c>zeeq_metric_events</c> (partition-pruned by <c>created_at_utc</c>), and the review
/// aggregates hit the <c>(organization_id, repository_id|author_login, created_at_utc)</c> indexes
/// on <c>code_review_records</c>. No JOINs on the metric-events path (Read-4). Interpolated
/// <c>Database.SqlQuery&lt;T&gt;</c> parameterizes every value; the only literal SQL fragments are
/// group-by column names chosen from closed enums (never caller input).
/// </remarks>
internal sealed class PostgresMetricsQueryStore(PostgresDbContext db) : IMetricsQueryStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<MetricSeriesPoint>> GetSeriesAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        MetricSeriesGroup groupBy,
        MetricSeriesFilters filters,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;
        var users = filters.Users ?? [];
        var tools = filters.Tools ?? [];
        var libraries = filters.Libraries ?? [];

        var seriesKey = MetricSeriesKeyExpression(groupBy);

        // GROUP BY 1, 2 groups by (bucket, series_key). For the ungrouped case series_key is a
        // constant NULL::text on every row, so grouping collapses to one row per bucket (SeriesKey
        // null) — the intended single-aggregate-series shape. Verified by the None-group test.
        var format = $$"""
            SELECT date_bin({0}, created_at_utc, {1}) AS bucket,
                   {{seriesKey}} AS series_key,
                   SUM(metric_value) AS value
            FROM zeeq.zeeq_metric_events
            WHERE organization_id = {2}
              AND metric_type = {3}
              AND created_at_utc >= {1}
              AND (cardinality({4}) = 0 OR user_email = ANY({4}))
              AND (cardinality({5}) = 0 OR tool_name = ANY({5}))
              AND (cardinality({6}) = 0 OR library = ANY({6}))
            GROUP BY 1, 2
            ORDER BY 1
            """;

        var sql = FormattableStringFactory.Create(
            format,
            range.Bucket,
            windowStart,
            organizationId,
            metricType,
            users,
            tools,
            libraries
        );

        return await db
            .Database.SqlQuery<MetricSeriesPoint>(sql)
            .TagWithOperationCallSite("metrics.series")
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetricTwoDimensionalSeriesPoint>> GetTwoDimensionalSeriesAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        MetricSeriesGroup primaryGroupBy,
        MetricSeriesGroup secondaryGroupBy,
        MetricSeriesFilters filters,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;
        var users = filters.Users ?? [];
        var tools = filters.Tools ?? [];
        var libraries = filters.Libraries ?? [];
        var primarySeriesKey = MetricSeriesKeyExpression(primaryGroupBy);
        var secondarySeriesKey = MetricSeriesKeyExpression(secondaryGroupBy);

        var format = $$"""
            SELECT date_bin({0}, created_at_utc, {1}) AS bucket,
                   {{primarySeriesKey}} AS primary_series_key,
                   {{secondarySeriesKey}} AS secondary_series_key,
                   SUM(metric_value) AS value
            FROM zeeq.zeeq_metric_events
            WHERE organization_id = {2}
              AND metric_type = {3}
              AND created_at_utc >= {1}
              AND (cardinality({4}) = 0 OR user_email = ANY({4}))
              AND (cardinality({5}) = 0 OR tool_name = ANY({5}))
              AND (cardinality({6}) = 0 OR library = ANY({6}))
            GROUP BY 1, 2, 3
            ORDER BY 1, 2, 3
            """;

        var sql = FormattableStringFactory.Create(
            format,
            range.Bucket,
            windowStart,
            organizationId,
            metricType,
            users,
            tools,
            libraries
        );

        return await db
            .Database.SqlQuery<MetricTwoDimensionalSeriesPoint>(sql)
            .TagWithOperationCallSite("metrics.series.two_dimensional")
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetricPercentilePoint>> GetPercentileSeriesAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        string? repositoryId,
        string? facet,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;

        FormattableString sql = $"""
            SELECT date_bin({range.Bucket}, created_at_utc, {windowStart}) AS bucket,
                   percentile_cont(0.50) WITHIN GROUP (ORDER BY metric_value) AS p50,
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY metric_value) AS p95,
                   percentile_cont(0.99) WITHIN GROUP (ORDER BY metric_value) AS p99
            FROM zeeq.zeeq_metric_events
            WHERE organization_id = {organizationId}
              AND metric_type = {metricType}
              AND created_at_utc >= {windowStart}
              AND ({repositoryId}::text IS NULL OR repository_id = {repositoryId}::text)
              AND ({facet}::text IS NULL OR facet = {facet}::text)
            GROUP BY 1
            ORDER BY 1
            """;

        return await db
            .Database.SqlQuery<MetricPercentilePoint>(sql)
            .TagWithOperationCallSite("metrics.percentiles")
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetricScatterPoint>> GetScatterSampleAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        string? repositoryId,
        string? facet,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;

        FormattableString sql = $"""
            SELECT created_at_utc,
                   metric_value,
                   (tags->>'tokens')::double precision AS tokens
            FROM zeeq.zeeq_metric_events
            WHERE organization_id = {organizationId}
              AND metric_type = {metricType}
              AND created_at_utc >= {windowStart}
              AND ({repositoryId}::text IS NULL OR repository_id = {repositoryId}::text)
              AND ({facet}::text IS NULL OR facet = {facet}::text)
            ORDER BY created_at_utc DESC
            LIMIT {limit}
            """;

        return await db
            .Database.SqlQuery<MetricScatterPoint>(sql)
            .TagWithOperationCallSite("metrics.scatter")
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetricLeaderboardItem>> GetLeaderboardAsync(
        string organizationId,
        string[] metricTypes,
        MetricWindow window,
        string? library,
        int top,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;

        FormattableString sql = $"""
            SELECT tags->>'path' AS item, library, SUM(metric_value) AS value
            FROM zeeq.zeeq_metric_events
            WHERE organization_id = {organizationId}
              AND metric_type = ANY({metricTypes})
              AND created_at_utc >= {windowStart}
              AND tags->>'path' IS NOT NULL
              AND ({library}::text IS NULL OR library = {library}::text)
            GROUP BY 1, 2
            ORDER BY 3 DESC
            LIMIT {top}
            """;

        return await db
            .Database.SqlQuery<MetricLeaderboardItem>(sql)
            .TagWithOperationCallSite("metrics.leaderboard")
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetricLeaderboardItem>> GetSectionLeaderboardAsync(
        string organizationId,
        string[] metricTypes,
        MetricWindow window,
        string? library,
        int top,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;

        // Same shape as GetLeaderboardAsync, one level finer: aggregated by (path, heading) instead
        // of path alone, so two sections in the same document count as distinct items. `heading` is
        // bounded by ingested content (same cardinality class as `path`), not by traffic. The
        // displayed `item` is heading-only (the path is redundant once the panel is already scoped
        // to one kind) — grouping still includes path so two different documents that happen to
        // share heading text don't have their counts merged, only their display label collides.
        FormattableString sql = $"""
            SELECT tags->>'heading' AS item, library, SUM(metric_value) AS value
            FROM zeeq.zeeq_metric_events
            WHERE organization_id = {organizationId}
              AND metric_type = ANY({metricTypes})
              AND created_at_utc >= {windowStart}
              AND tags->>'path' IS NOT NULL
              AND tags->>'heading' IS NOT NULL
              AND ({library}::text IS NULL OR library = {library}::text)
            GROUP BY tags->>'path', tags->>'heading', library
            ORDER BY 3 DESC
            LIMIT {top}
            """;

        return await db
            .Database.SqlQuery<MetricLeaderboardItem>(sql)
            .TagWithOperationCallSite("metrics.leaderboard.sections")
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewVolumePoint>> GetReviewVolumeSeriesAsync(
        string organizationId,
        MetricWindow window,
        string[]? repositoryIds,
        string[]? authorLogins,
        CodeReviewRequestOrigin? requestOrigin,
        ReviewVolumeGroup groupBy,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;
        var repos = repositoryIds ?? [];
        var authors = authorLogins ?? [];
        var origin = requestOrigin?.ToString();

        var seriesKey = groupBy switch
        {
            ReviewVolumeGroup.Repo => "repository_id",
            ReviewVolumeGroup.Author => "author_login",
            ReviewVolumeGroup.Origin => "request_origin",
            _ => "NULL::text",
        };

        var format = $$"""
            SELECT date_bin({0}, created_at_utc, {1}) AS bucket,
                   {{seriesKey}} AS series_key,
                   COUNT(*) AS count
            FROM zeeq.code_review_records
            WHERE organization_id = {2}
              AND created_at_utc >= {1}
              AND (cardinality({3}) = 0 OR repository_id = ANY({3}))
              AND (cardinality({4}) = 0 OR author_login = ANY({4}))
              AND ({5}::text IS NULL OR request_origin = {5}::text)
            GROUP BY 1, 2
            ORDER BY 1
            """;

        var sql = FormattableStringFactory.Create(
            format,
            range.Bucket,
            windowStart,
            organizationId,
            repos,
            authors,
            origin
        );

        var points = await db
            .Database.SqlQuery<ReviewVolumePoint>(sql)
            .TagWithOperationCallSite("metrics.reviews.volume")
            .ToListAsync(cancellationToken);

        return groupBy == ReviewVolumeGroup.Repo
            ? await RelabelRepositoriesAsync(
                points,
                organizationId,
                static point => point.SeriesKey,
                static (point, name) => point with { SeriesKey = name },
                cancellationToken
            )
            : points;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewFindingsPoint>> GetReviewFindingsSeriesAsync(
        string organizationId,
        MetricWindow window,
        string[]? repositoryIds,
        string[]? authorLogins,
        ReviewFindingsGroup groupBy,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;
        var repos = repositoryIds ?? [];
        var authors = authorLogins ?? [];

        var seriesKey = groupBy switch
        {
            ReviewFindingsGroup.Repo => "repository_id",
            ReviewFindingsGroup.Author => "author_login",
            ReviewFindingsGroup.Origin => "request_origin",
            _ => "NULL::text",
        };

        var format = $$"""
            SELECT date_bin({0}, created_at_utc, {1}) AS bucket,
                   {{seriesKey}} AS series_key,
                   COALESCE(SUM(critical_findings), 0) AS critical,
                   COALESCE(SUM(major_findings), 0) AS major,
                   COALESCE(SUM(minor_findings), 0) AS minor,
                   COALESCE(SUM(suggestion_findings), 0) AS suggestion,
                   COALESCE(SUM(comment_findings), 0) AS comment
            FROM zeeq.code_review_records
            WHERE organization_id = {2}
              AND created_at_utc >= {1}
              AND (cardinality({3}) = 0 OR repository_id = ANY({3}))
              AND (cardinality({4}) = 0 OR author_login = ANY({4}))
            GROUP BY 1, 2
            ORDER BY 1
            """;

        var sql = FormattableStringFactory.Create(
            format,
            range.Bucket,
            windowStart,
            organizationId,
            repos,
            authors
        );

        var points = await db
            .Database.SqlQuery<ReviewFindingsPoint>(sql)
            .TagWithOperationCallSite("metrics.reviews.findings")
            .ToListAsync(cancellationToken);

        return groupBy == ReviewFindingsGroup.Repo
            ? await RelabelRepositoriesAsync(
                points,
                organizationId,
                static point => point.SeriesKey,
                static (point, name) => point with { SeriesKey = name },
                cancellationToken
            )
            : points;
    }

    /// <inheritdoc />
    public async Task<MetricsOverview> GetOverviewAsync(
        string organizationId,
        MetricWindow window,
        CancellationToken cancellationToken
    )
    {
        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;

        FormattableString metricSql = $"""
            SELECT
              COALESCE(
                SUM(metric_value) FILTER (WHERE metric_type = 'zeeq_tool_call_counter'), 0
              )::double precision AS tool_calls,
              COALESCE(
                SUM(metric_value) FILTER (
                  WHERE metric_type IN (
                    'zeeq_document_read_counter',
                    'zeeq_section_read_counter',
                    'zeeq_snippet_read_counter'
                  )
                ), 0
              )::double precision AS knowledge_reads,
              COALESCE(
                percentile_cont(0.95) WITHIN GROUP (ORDER BY metric_value) FILTER (
                  WHERE metric_type = 'zeeq_review_duration_ms'
                ), 0
              )::double precision AS review_duration_ms
            FROM zeeq.zeeq_metric_events
            WHERE organization_id = {organizationId}
              AND created_at_utc >= {windowStart}
            """;

        var metricRow = await db
            .Database.SqlQuery<OverviewMetricRow>(metricSql)
            .TagWithOperationCallSite("metrics.overview.metrics")
            .SingleAsync(cancellationToken);

        FormattableString reviewSql = $"""
            SELECT COUNT(*) AS reviews,
                   COALESCE(SUM(critical_findings), 0) AS critical_findings,
                   COALESCE(SUM(major_findings), 0) AS major_findings
            FROM zeeq.code_review_records
            WHERE organization_id = {organizationId}
              AND created_at_utc >= {windowStart}
            """;

        var reviewRow = await db
            .Database.SqlQuery<OverviewReviewRow>(reviewSql)
            .TagWithOperationCallSite("metrics.overview.reviews")
            .SingleAsync(cancellationToken);

        return new MetricsOverview(
            metricRow.ToolCalls,
            metricRow.KnowledgeReads,
            reviewRow.Reviews,
            reviewRow.CriticalFindings,
            reviewRow.MajorFindings,
            metricRow.ReviewDurationMs
        );
    }

    /// <inheritdoc />
    public async Task<MetricsFilterOptions> GetFilterOptionsAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        // Filter values sourced from the data itself (no static lists, no GitHub-App dependency).
        // Repositories come from code_review_repositories including soft-deleted rows so historical
        // review data whose mapping was removed stays filterable.
        // NOTE: DisplayName is returned raw; duplicate repo display names are disambiguated at the
        // presentation layer (the web Home view suffixes a short id) so this endpoint stays a pure
        // data source. All four reads are AsNoTracking projections, single-table, endpoint-cached.
        var users = await db
            .MetricEvents.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.UserEmail != null)
            .Select(e => e.UserEmail!)
            .Distinct()
            .OrderBy(value => value)
            .TagWithOperationCallSite("metrics.options.users")
            .ToListAsync(cancellationToken);

        var tools = await db
            .MetricEvents.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.ToolName != null)
            .Select(e => e.ToolName!)
            .Distinct()
            .OrderBy(value => value)
            .TagWithOperationCallSite("metrics.options.tools")
            .ToListAsync(cancellationToken);

        var repositories = await db.Set<CodeRepository>()
            .AsNoTracking()
            .Where(repository => repository.OrganizationId == organizationId)
            .OrderBy(repository => repository.DisplayName)
            .Select(repository => new MetricsRepositoryOption(
                repository.Id,
                repository.DisplayName
            ))
            .TagWithOperationCallSite("metrics.options.repositories")
            .ToListAsync(cancellationToken);

        var authors = await db
            .CodeReviewRecords.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.AuthorLogin != "")
            .Select(r => r.AuthorLogin)
            .Distinct()
            .OrderBy(value => value)
            .TagWithOperationCallSite("metrics.options.authors")
            .ToListAsync(cancellationToken);

        return new MetricsFilterOptions(users, tools, repositories, authors);
    }

    private sealed record OverviewMetricRow(
        double ToolCalls,
        double KnowledgeReads,
        double ReviewDurationMs
    );

    private sealed record OverviewReviewRow(
        long Reviews,
        long CriticalFindings,
        long MajorFindings
    );

    /// <summary>
    /// Replaces repository-id series keys with the repository display name via a single-table,
    /// cache-fronted lookup (no JOIN — Read-4). Ids with no matching repository row keep the raw id.
    /// </summary>
    private async Task<IReadOnlyList<T>> RelabelRepositoriesAsync<T>(
        IReadOnlyList<T> points,
        string organizationId,
        Func<T, string?> seriesKeyOf,
        Func<T, string, T> withSeriesKey,
        CancellationToken cancellationToken
    )
    {
        var names = await ResolveRepositoryDisplayNamesAsync(organizationId, cancellationToken);
        if (names.Count == 0)
        {
            return points;
        }

        return
        [
            .. points.Select(point =>
                seriesKeyOf(point) is { } id && names.TryGetValue(id, out var name)
                    ? withSeriesKey(point, name)
                    : point
            ),
        ];
    }

    /// <summary>
    /// Builds an id → display-name map for every repository in the org. Reads all rows regardless of
    /// enabled/disabled state because historical review rows can reference a since-removed
    /// (soft-deleted) repository. Repos that share a display name get a short id suffix so distinct
    /// repositories never collapse into one series once the client pivots by series key. The whole
    /// read endpoint is cached, so this lookup only runs on a cache miss.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveRepositoryDisplayNamesAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var rows = await db.Set<CodeRepository>()
            .Where(repository => repository.OrganizationId == organizationId)
            .Select(repository => new { repository.Id, repository.DisplayName })
            .TagWithOperationCallSite("metrics.reviews.repository-names")
            .ToListAsync(cancellationToken);

        var duplicated = rows.GroupBy(row => row.DisplayName)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        return rows.ToDictionary(
            row => row.Id,
            row =>
                duplicated.Contains(row.DisplayName)
                    ? $"{row.DisplayName} ({ShortId(row.Id)})"
                    : row.DisplayName
        );
    }

    private static string ShortId(string id) => id.Length <= 8 ? id : id[^8..];

    private static string MetricSeriesKeyExpression(MetricSeriesGroup groupBy) =>
        groupBy switch
        {
            MetricSeriesGroup.User => "user_email",
            MetricSeriesGroup.Tool => "tool_name",
            MetricSeriesGroup.Library => "library",
            MetricSeriesGroup.UserAgent => "tags->>'user_agent'",
            MetricSeriesGroup.Model => "tags->>'model'",
            _ => "NULL::text",
        };
}
