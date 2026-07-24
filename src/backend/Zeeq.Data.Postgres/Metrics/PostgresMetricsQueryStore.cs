using System.Diagnostics;
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
/// on <c>code_review_records</c>. Interpolated
/// <c>Database.SqlQuery&lt;T&gt;</c> parameterizes every value; the only literal SQL fragments are
/// group-by column names chosen from closed enums (never caller input).
/// </remarks>
internal sealed class PostgresMetricsQueryStore(PostgresDbContext db) : IMetricsQueryStore
{
    // NOTE: Alias resolution is intentionally SQL-local here. These queries
    // aggregate directly over raw metric rows, so the equivalent trim/lower
    // normalization must remain SQL-translatable rather than calling the C#
    // UserAliasNormalizer. The active alias unique index on (organization_id,
    // kind, normalized_value) guarantees at most one active email alias match
    // per metric row, so the left join does not multiply metric values.
    // NOTE: User filters are canonical metric user keys from GetFilterOptionsAsync,
    // not raw telemetry owner emails; alias owner emails roll up before filtering.
    // NOTE: alias_user.email can be NULL even when the alias join matches an
    // existing core_users row -- User.Email is nullable (e.g. a user whose IdP
    // login never surfaced an email claim). metric.user_email must be tried
    // before the bare user_id: it's the telemetry-reported email that was used
    // to find this alias row in the first place, so it's always a real email
    // when the alias matched at all. user_id is the last resort only for the
    // (effectively unreachable given the WHERE/JOIN clauses) case where no
    // email is available anywhere.
    private const string ResolvedMetricUserKeySql =
        "COALESCE(alias_user.email, metric.user_email, user_email_alias.user_id)";
    private const string NormalizedMetricUserEmailSql = "lower(btrim(metric.user_email))";

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

        var seriesKey = MetricSeriesKeyExpression(groupBy, "metric");
        var userFilterKey = MetricSeriesKeyExpression(MetricSeriesGroup.User, "metric");

        // GROUP BY 1, 2 groups by (bucket, series_key). For the ungrouped case series_key is a
        // constant NULL::text on every row, so grouping collapses to one row per bucket (SeriesKey
        // null) — the intended single-aggregate-series shape. Verified by the None-group test.
        var format = $$"""
            SELECT date_bin({0}, metric.created_at_utc, {1}) AS bucket,
                   {{seriesKey}} AS series_key,
                   SUM(metric.metric_value) AS value
            FROM zeeq.zeeq_metric_events metric
            LEFT JOIN zeeq.core_user_aliases user_email_alias
             ON user_email_alias.organization_id = metric.organization_id
             AND user_email_alias.kind = 'Email'
             AND user_email_alias.normalized_value = {{NormalizedMetricUserEmailSql}}
             AND user_email_alias.disabled_at_utc IS NULL
            LEFT JOIN zeeq.core_users alias_user
              ON alias_user.id = user_email_alias.user_id
            WHERE metric.organization_id = {2}
              AND metric.metric_type = {3}
              AND metric.created_at_utc >= {1}
              AND (cardinality({4}) = 0 OR {{userFilterKey}} = ANY({4}))
              AND (cardinality({5}) = 0 OR metric.tool_name = ANY({5}))
              AND (cardinality({6}) = 0 OR metric.library = ANY({6}))
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
        var primarySeriesKey = MetricSeriesKeyExpression(primaryGroupBy, "metric");
        var secondarySeriesKey = MetricSeriesKeyExpression(secondaryGroupBy, "metric");
        var userFilterKey = MetricSeriesKeyExpression(MetricSeriesGroup.User, "metric");

        var format = $$"""
            SELECT date_bin({0}, metric.created_at_utc, {1}) AS bucket,
                   {{primarySeriesKey}} AS primary_series_key,
                   {{secondarySeriesKey}} AS secondary_series_key,
                   SUM(metric.metric_value) AS value
            FROM zeeq.zeeq_metric_events metric
            LEFT JOIN zeeq.core_user_aliases user_email_alias
             ON user_email_alias.organization_id = metric.organization_id
             AND user_email_alias.kind = 'Email'
             AND user_email_alias.normalized_value = {{NormalizedMetricUserEmailSql}}
             AND user_email_alias.disabled_at_utc IS NULL
            LEFT JOIN zeeq.core_users alias_user
              ON alias_user.id = user_email_alias.user_id
            WHERE metric.organization_id = {2}
              AND metric.metric_type = {3}
              AND metric.created_at_utc >= {1}
              AND (cardinality({4}) = 0 OR {{userFilterKey}} = ANY({4}))
              AND (cardinality({5}) = 0 OR metric.tool_name = ANY({5}))
              AND (cardinality({6}) = 0 OR metric.library = ANY({6}))
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
    /// <remarks>
    /// A single scan, window-partitioned twice: <c>SUM(...) OVER (PARTITION BY review_group_id)</c>
    /// computes each group's window-total findings (this is what the returned
    /// <see cref="FindingReviewGroup.GroupCriticalFindings"/>/<see cref="FindingReviewGroup.GroupMajorFindings"/>
    /// report — see the remarks on that type for why the totals, not the latest attempt's own count,
    /// are what's returned), and <c>ROW_NUMBER() OVER (PARTITION BY review_group_id ORDER BY
    /// created_at_utc DESC, id DESC)</c> identifies the latest attempt per group so identity fields
    /// (title, author, …) come from the current state, not a stale one. The <c>id</c> tie-breaker
    /// matches the outer/cursor ordering (<c>review_created_at_utc DESC, review_id DESC</c>) so the
    /// selected "latest" row is deterministic even when two attempts in the same group share a
    /// timestamp — without it, <c>rn = 1</c> could pick either row on different executions.
    /// <para>
    /// The window bound (<c>created_at_utc &gt;= windowStart</c>, applied inside the CTE before either
    /// window function runs) is one-sided — there is no upper bound besides "now" — matching
    /// <see cref="GetOverviewAsync"/>'s same query shape. This means a group can never have its true
    /// latest attempt fall outside the window while an earlier attempt falls inside: the true latest
    /// (by definition the max <c>created_at_utc</c> across the whole group) is always
    /// &gt;= any single in-window row's timestamp, so if any row qualifies, the true latest also
    /// qualifies and is present in the scanned set. <c>rn = 1</c> therefore always lands on the actual
    /// latest attempt, never a stale earlier one — the CTE's window filter and the dedup step can't
    /// disagree the way they could with a two-sided (start-and-end) window.
    /// </para>
    /// <para>
    /// Both windows run over the same <c>organization_id + created_at_utc</c>-bounded set already used
    /// by <see cref="GetOverviewAsync"/>, so this stays partition-pruned. The <c>LEFT JOIN</c> to
    /// <c>code_review_pull_request_records</c> only touches the (already limited) post-filter row set,
    /// not the windowed scan, and supplies the PR's own <c>created_at_utc</c> — needed because the
    /// PR-view deep link token is keyed by the pull request's partition timestamp, not the review's
    /// (see <c>CodeReviewRequestLinkFactory.BuildSingleReviewLink</c> vs the check-run service's
    /// PR-view link builder).
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<FindingReviewGroup>> ListFindingReviewGroupsAsync(
        string organizationId,
        MetricWindow window,
        FindingSeverity severity,
        DateTimeOffset? cursorCreatedAtUtc,
        string? cursorId,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (cursorCreatedAtUtc.HasValue != (cursorId is not null))
        {
            throw new ArgumentException(
                $"{nameof(cursorCreatedAtUtc)} and {nameof(cursorId)} must be supplied together."
            );
        }

        var range = window.ToRange();
        var windowStart = DateTimeOffset.UtcNow - range.Span;
        var hasCursor = cursorCreatedAtUtc.HasValue;

        // Exhaustive by construction: the handler rejects any FindingSeverity outside this closed
        // enum with a 400 before calling here, so an undefined value can never reach this switch.
        var severityColumn = severity switch
        {
            FindingSeverity.Critical => "group_critical_findings",
            FindingSeverity.Major => "group_major_findings",
            _ => throw new UnreachableException(
                $"{nameof(FindingSeverity)} '{severity}' should have been rejected by the endpoint handler."
            ),
        };

        var format = $$"""
            WITH windowed AS (
                SELECT
                    id AS review_id,
                    created_at_utc AS review_created_at_utc,
                    title,
                    owner_qualified_repo_name,
                    pull_request_number,
                    author_login,
                    request_origin,
                    pull_request_record_id,
                    SUM(critical_findings) OVER (PARTITION BY review_group_id) AS group_critical_findings,
                    SUM(major_findings) OVER (PARTITION BY review_group_id) AS group_major_findings,
                    ROW_NUMBER() OVER (
                        PARTITION BY review_group_id ORDER BY created_at_utc DESC, id DESC
                    ) AS rn
                FROM zeeq.code_review_records
                WHERE organization_id = {0}
                  AND created_at_utc >= {1}
            )
            SELECT
                windowed.review_id,
                windowed.review_created_at_utc,
                windowed.title,
                windowed.owner_qualified_repo_name,
                windowed.pull_request_number,
                windowed.author_login,
                windowed.request_origin::text AS request_origin,
                windowed.pull_request_record_id,
                windowed.group_critical_findings,
                windowed.group_major_findings,
                pr.created_at_utc AS pull_request_record_created_at_utc
            FROM windowed
            LEFT JOIN zeeq.code_review_pull_request_records pr
              ON pr.id = windowed.pull_request_record_id
            WHERE windowed.rn = 1
              AND windowed.{{severityColumn}} > 0
              AND ({2} = false OR (
                    windowed.review_created_at_utc < {3}
                    OR (windowed.review_created_at_utc = {3} AND windowed.review_id < {4})
                  ))
            ORDER BY windowed.review_created_at_utc DESC, windowed.review_id DESC
            LIMIT {5}
            """;

        var sql = FormattableStringFactory.Create(
            format,
            organizationId,
            windowStart,
            hasCursor,
            cursorCreatedAtUtc ?? DateTimeOffset.UnixEpoch,
            cursorId ?? "",
            limit
        );

        var rows = await db
            .Database.SqlQuery<FindingReviewRow>(sql)
            .TagWithOperationCallSite("metrics.reviews.findings.list")
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToFindingReviewGroup)];
    }

    /// <summary>Maps one raw finding-review-group SQL row to the public store contract, parsing the request-origin text.</summary>
    private static FindingReviewGroup ToFindingReviewGroup(FindingReviewRow row) =>
        new(
            ReviewId: row.ReviewId,
            ReviewCreatedAtUtc: row.ReviewCreatedAtUtc,
            Title: row.Title,
            OwnerQualifiedRepoName: row.OwnerQualifiedRepoName,
            PullRequestNumber: row.PullRequestNumber,
            AuthorLogin: row.AuthorLogin,
            RequestOrigin: Enum.Parse<CodeReviewRequestOrigin>(row.RequestOrigin),
            PullRequestRecordId: row.PullRequestRecordId,
            PullRequestRecordCreatedAtUtc: row.PullRequestRecordCreatedAtUtc,
            GroupCriticalFindings: row.GroupCriticalFindings,
            GroupMajorFindings: row.GroupMajorFindings
        );

    /// <summary>Raw SQL projection for <see cref="ListFindingReviewGroupsAsync"/>, before request-origin parsing.</summary>
    private sealed record FindingReviewRow(
        string ReviewId,
        DateTimeOffset ReviewCreatedAtUtc,
        string Title,
        string OwnerQualifiedRepoName,
        int PullRequestNumber,
        string AuthorLogin,
        string RequestOrigin,
        string? PullRequestRecordId,
        long GroupCriticalFindings,
        long GroupMajorFindings,
        DateTimeOffset? PullRequestRecordCreatedAtUtc
    );

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
        // ResolvedMetricUserKeySql is a closed, hardcoded column expression, not caller
        // input -- it must be spliced into the SQL text directly rather than bound as a
        // query parameter, so this builds a plain format string first (organizationId
        // left as a "{0}" placeholder) and only turns it into a FormattableString
        // afterward, the same two-stage pattern GetSeriesAsync uses below.
        var usersFormat = $$"""
            SELECT DISTINCT {{ResolvedMetricUserKeySql}} AS "Value"
            FROM zeeq.zeeq_metric_events metric
            LEFT JOIN zeeq.core_user_aliases user_email_alias
             ON user_email_alias.organization_id = metric.organization_id
             AND user_email_alias.kind = 'Email'
             AND user_email_alias.normalized_value = lower(btrim(metric.user_email))
             AND user_email_alias.disabled_at_utc IS NULL
            LEFT JOIN zeeq.core_users alias_user
              ON alias_user.id = user_email_alias.user_id
            WHERE metric.organization_id = {0}
              AND metric.user_email IS NOT NULL
            ORDER BY 1
            """;
        var usersSql = FormattableStringFactory.Create(usersFormat, organizationId);

        var users = await db
            .Database.SqlQuery<string>(usersSql)
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

    private static string MetricSeriesKeyExpression(MetricSeriesGroup groupBy, string tableAlias) =>
        groupBy switch
        {
            MetricSeriesGroup.User => ResolvedMetricUserKeySql,
            MetricSeriesGroup.Tool => $"{tableAlias}.tool_name",
            MetricSeriesGroup.Library => $"{tableAlias}.library",
            MetricSeriesGroup.UserAgent => $"{tableAlias}.tags->>'user_agent'",
            MetricSeriesGroup.Model => $"{tableAlias}.tags->>'model'",
            _ => "NULL::text",
        };
}
