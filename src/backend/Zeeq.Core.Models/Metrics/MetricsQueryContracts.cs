namespace Zeeq.Core.Models;

/// <summary>
/// One bucketed point in a metric time series, optionally grouped by a single dimension.
/// </summary>
/// <param name="Bucket">Fixed-width bucket start (from <c>date_bin</c>).</param>
/// <param name="SeriesKey">Group value (user, tool, library, …) or null when ungrouped.</param>
/// <param name="Value">Summed metric value for the bucket/series.</param>
public sealed record MetricSeriesPoint(DateTimeOffset Bucket, string? SeriesKey, double Value);

/// <summary>
/// One bucketed point in a metric time series grouped by two dimensions.
/// </summary>
/// <param name="Bucket">Fixed-width bucket start (from <c>date_bin</c>).</param>
/// <param name="PrimarySeriesKey">Primary group value (for example model).</param>
/// <param name="SecondarySeriesKey">Secondary group value (for example user).</param>
/// <param name="Value">Summed metric value for the bucket/dimension pair.</param>
public sealed record MetricTwoDimensionalSeriesPoint(
    DateTimeOffset Bucket,
    string? PrimarySeriesKey,
    string? SecondarySeriesKey,
    double Value
);

/// <summary>One bucket's p50/p95/p99 for a histogram metric (UI-8/UI-9).</summary>
public sealed record MetricPercentilePoint(
    DateTimeOffset Bucket,
    double P50,
    double P95,
    double P99
);

/// <summary>One raw sample for a duration-vs-tokens scatter (UI-8/UI-9).</summary>
/// <param name="CreatedAtUtc">Sample capture time.</param>
/// <param name="MetricValue">The measured value (for example elapsed ms).</param>
/// <param name="Tokens">Token count from the <c>tokens</c> tag, when present.</param>
public sealed record MetricScatterPoint(
    DateTimeOffset CreatedAtUtc,
    double MetricValue,
    double? Tokens
);

/// <summary>One ranked leaderboard item (UI-7): a path with its owning library and total.</summary>
public sealed record MetricLeaderboardItem(string Item, string? Library, double Value);

/// <summary>Bucketed review volume, optionally grouped by repository/author/origin (UI-4/UI-5).</summary>
public sealed record ReviewVolumePoint(DateTimeOffset Bucket, string? SeriesKey, long Count);

/// <summary>Bucketed finding-severity sums, optionally grouped by repository/author (UI-3).</summary>
public sealed record ReviewFindingsPoint(
    DateTimeOffset Bucket,
    string? SeriesKey,
    long Critical,
    long Major,
    long Minor,
    long Suggestion,
    long Comment
);

/// <summary>Headline stat-card numbers for the overview tab.</summary>
public sealed record MetricsOverview(
    double ToolCalls,
    double KnowledgeReads,
    long Reviews,
    long CriticalFindings,
    long MajorFindings,
    double P95ReviewDurationMs
);

/// <summary>One selectable repository option for the review filters (id + display name).</summary>
/// <remarks>Includes soft-deleted repositories so historical review data stays filterable.</remarks>
public sealed record MetricsRepositoryOption(string Id, string DisplayName);

/// <summary>Distinct filter values available across the org's metric + review data.</summary>
public sealed record MetricsFilterOptions(
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Tools,
    IReadOnlyList<MetricsRepositoryOption> Repositories,
    IReadOnlyList<string> Authors
);

/// <summary>Optional multi-select filters for a metric series query.</summary>
public sealed record MetricSeriesFilters(
    string[]? Users = null,
    string[]? Tools = null,
    string[]? Libraries = null
);

/// <summary>Group dimension for a metric series.</summary>
public enum MetricSeriesGroup
{
    /// <summary>No grouping; a single aggregate series.</summary>
    None,

    /// <summary>Group by <c>user_email</c>.</summary>
    User,

    /// <summary>Group by <c>tool_name</c>.</summary>
    Tool,

    /// <summary>Group by <c>library</c>.</summary>
    Library,

    /// <summary>Group by the <c>user_agent</c> jsonb tag (UI-2).</summary>
    UserAgent,

    /// <summary>Group by the <c>model</c> jsonb tag from agent telemetry.</summary>
    Model,
}

/// <summary>Group dimension for review-volume series.</summary>
public enum ReviewVolumeGroup
{
    /// <summary>Group by repository.</summary>
    Repo,

    /// <summary>Group by author login.</summary>
    Author,

    /// <summary>Group by request origin (agent vs PR).</summary>
    Origin,
}

/// <summary>Group dimension for review-findings series.</summary>
public enum ReviewFindingsGroup
{
    /// <summary>Group by repository.</summary>
    Repo,

    /// <summary>Group by author login.</summary>
    Author,

    /// <summary>Group by request origin (agent vs PR).</summary>
    Origin,
}

/// <summary>Severity dimension for the findings drill-down list (the Critical/Major stat-card slideover).</summary>
public enum FindingSeverity
{
    /// <summary>List review groups with at least one critical finding in the window.</summary>
    Critical,

    /// <summary>List review groups with at least one major finding in the window.</summary>
    Major,
}

/// <summary>
/// One PR (or agent-session) review group surfaced by the findings drill-down list, deduplicated to
/// its latest review attempt within the window.
/// </summary>
/// <remarks>
/// A pull request can be reviewed multiple times (each push re-triggers a review), and every attempt
/// is its own <c>code_review_records</c> row sharing one <c>review_group_id</c>. Showing one row per
/// attempt would both spam the list and go stale the moment a follow-up review fixes something, so
/// this is pre-grouped by <c>review_group_id</c>: identity fields (<see cref="ReviewId"/>,
/// <see cref="Title"/>, …) come from the latest attempt, but <see cref="GroupCriticalFindings"/> /
/// <see cref="GroupMajorFindings"/> sum every attempt in the group within the window — so the sum of
/// this field across every returned group reconciles exactly with the headline
/// <see cref="MetricsOverview.CriticalFindings"/> / <see cref="MetricsOverview.MajorFindings"/> count,
/// which is itself a sum over every review row, not just the latest per PR. See
/// <c>PostgresMetricsQueryStore.ListFindingReviewGroupsAsync</c> for the query.
/// </remarks>
/// <param name="ReviewId">Latest review record id in the group; identifies the review to link to.</param>
/// <param name="ReviewCreatedAtUtc">Latest review's creation timestamp; also the keyset-pagination cursor value.</param>
/// <param name="Title">Pull request title, or the review title for agent (non-PR) reviews.</param>
/// <param name="OwnerQualifiedRepoName">Provider-qualified repository name, such as owner/repo.</param>
/// <param name="PullRequestNumber">Provider pull request number; 0 for agent reviews with no PR.</param>
/// <param name="AuthorLogin">Provider login of the pull request author, or the review's recorded author for agent reviews.</param>
/// <param name="RequestOrigin">Source that requested the latest review in the group.</param>
/// <param name="PullRequestRecordId">Stable pull request record id, when the group is PR-backed; null for agent reviews with no resolved PR.</param>
/// <param name="PullRequestRecordCreatedAtUtc">The pull request record's own creation timestamp (its partition key); null when <see cref="PullRequestRecordId"/> is null.</param>
/// <param name="GroupCriticalFindings">Sum of critical findings across every review attempt in the group within the window.</param>
/// <param name="GroupMajorFindings">Sum of major findings across every review attempt in the group within the window.</param>
public sealed record FindingReviewGroup(
    string ReviewId,
    DateTimeOffset ReviewCreatedAtUtc,
    string Title,
    string OwnerQualifiedRepoName,
    int PullRequestNumber,
    string AuthorLogin,
    CodeReviewRequestOrigin RequestOrigin,
    string? PullRequestRecordId,
    DateTimeOffset? PullRequestRecordCreatedAtUtc,
    long GroupCriticalFindings,
    long GroupMajorFindings
);

/// <summary>
/// Read store for the metrics dashboard. Every query is single-organization, single-table, and
/// window-scoped (no JOINs on the metric-events path) so it stays partition-pruned and index-covered.
/// </summary>
public interface IMetricsQueryStore
{
    /// <summary>Bucketed <c>SUM(metric_value)</c> series, optionally grouped by one dimension.</summary>
    Task<IReadOnlyList<MetricSeriesPoint>> GetSeriesAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        MetricSeriesGroup groupBy,
        MetricSeriesFilters filters,
        CancellationToken cancellationToken
    );

    /// <summary>Bucketed <c>SUM(metric_value)</c> series grouped by two dimensions.</summary>
    Task<IReadOnlyList<MetricTwoDimensionalSeriesPoint>> GetTwoDimensionalSeriesAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        MetricSeriesGroup primaryGroupBy,
        MetricSeriesGroup secondaryGroupBy,
        MetricSeriesFilters filters,
        CancellationToken cancellationToken
    );

    /// <summary>Per-bucket p50/p95/p99 for a histogram metric, optionally filtered by repo/facet.</summary>
    Task<IReadOnlyList<MetricPercentilePoint>> GetPercentileSeriesAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        string? repositoryId,
        string? facet,
        CancellationToken cancellationToken
    );

    /// <summary>Recent raw samples for a duration-vs-tokens scatter.</summary>
    Task<IReadOnlyList<MetricScatterPoint>> GetScatterSampleAsync(
        string organizationId,
        string metricType,
        MetricWindow window,
        string? repositoryId,
        string? facet,
        int limit,
        CancellationToken cancellationToken
    );

    /// <summary>Top-N ranked items across one or more metric types (UI-7 path leaderboard).</summary>
    Task<IReadOnlyList<MetricLeaderboardItem>> GetLeaderboardAsync(
        string organizationId,
        string[] metricTypes,
        MetricWindow window,
        string? library,
        int top,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Top-N ranked (path, heading) sections/snippets — the same shape as
    /// <see cref="GetLeaderboardAsync" /> but keyed one level finer than the document path, so two
    /// different sections in the same document rank separately.
    /// </summary>
    Task<IReadOnlyList<MetricLeaderboardItem>> GetSectionLeaderboardAsync(
        string organizationId,
        string[] metricTypes,
        MetricWindow window,
        string? library,
        int top,
        CancellationToken cancellationToken
    );

    /// <summary>Bucketed review volume from <c>code_review_records</c>, optionally grouped/filtered.</summary>
    Task<IReadOnlyList<ReviewVolumePoint>> GetReviewVolumeSeriesAsync(
        string organizationId,
        MetricWindow window,
        string[]? repositoryIds,
        string[]? authorLogins,
        CodeReviewRequestOrigin? requestOrigin,
        ReviewVolumeGroup groupBy,
        CancellationToken cancellationToken
    );

    /// <summary>Bucketed finding-severity sums from <c>code_review_records</c>, optionally grouped/filtered.</summary>
    Task<IReadOnlyList<ReviewFindingsPoint>> GetReviewFindingsSeriesAsync(
        string organizationId,
        MetricWindow window,
        string[]? repositoryIds,
        string[]? authorLogins,
        ReviewFindingsGroup groupBy,
        CancellationToken cancellationToken
    );

    /// <summary>Headline overview numbers for the current window.</summary>
    Task<MetricsOverview> GetOverviewAsync(
        string organizationId,
        MetricWindow window,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Newest-first, keyset-paginated review groups with at least one finding of the given severity
    /// in the window (drill-down list behind the Critical/Major findings stat cards).
    /// </summary>
    /// <param name="organizationId">Tenant scope for the query.</param>
    /// <param name="window">Dashboard time window.</param>
    /// <param name="severity">Which finding column gates and sorts the group total (critical or major).</param>
    /// <param name="cursorCreatedAtUtc">
    /// Exclusive lower-bound timestamp from a previous page's last item, or null for the first page.
    /// Must be supplied together with <paramref name="cursorId"/> — see
    /// <see cref="FindingReviewGroup.ReviewCreatedAtUtc"/>.
    /// </param>
    /// <param name="cursorId">Tie-breaker id pairing with <paramref name="cursorCreatedAtUtc"/>, or null for the first page.</param>
    /// <param name="limit">Maximum rows to return; callers pass page size + 1 to detect a next page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<FindingReviewGroup>> ListFindingReviewGroupsAsync(
        string organizationId,
        MetricWindow window,
        FindingSeverity severity,
        DateTimeOffset? cursorCreatedAtUtc,
        string? cursorId,
        int limit,
        CancellationToken cancellationToken
    );

    /// <summary>Distinct filter values (users, tools, repositories, authors) for the dashboard filters.</summary>
    Task<MetricsFilterOptions> GetFilterOptionsAsync(
        string organizationId,
        CancellationToken cancellationToken
    );
}
