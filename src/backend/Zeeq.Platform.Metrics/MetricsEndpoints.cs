using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Browser-authenticated read endpoints backing the home-page metrics dashboard.
/// </summary>
/// <remarks>
/// Organization scope comes from the route <c>{orgId}</c> validated against the auth cookie
/// (<see cref="RequireOrganizationActivationExtensions" />), never a free query parameter. Every
/// route is a cached, single-organization window query. Primitive inputs carry
/// length/range/allowed-value validators so oversized or out-of-set query strings are rejected
/// (and documented as enums in the OpenAPI schema) before any query runs.
/// </remarks>
public sealed class MetricsEndpoints : IEndpoint
{
    private const int MaxIdLength = 128;
    private const int MaxFacetLength = 64;
    private const int MaxFilterValues = 50;

    // Closed histogram/counter allow-sets, inlined so [AllowedValues] documents them as OpenAPI enums.
    private const string ToolCall = "zeeq_tool_call_counter";
    private const string UserAgent = "zeeq_user_agent_counter";
    private const string DocumentRead = "zeeq_document_read_counter";
    private const string SectionRead = "zeeq_section_read_counter";
    private const string SnippetRead = "zeeq_snippet_read_counter";
    private const string AgentTokenUsage = "zeeq_agent_token_usage";
    private const string AgentCostUsd = "zeeq_agent_cost_usd";
    private const string ReviewDuration = "zeeq_review_duration_ms";
    private const string ReviewTokens = "zeeq_review_tokens";
    private const string ReviewCost = "zeeq_review_cost_usd";

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/metrics")
            .WithTags("Metrics")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}/metrics/series/{metricType}
        group
            .MapGet(
                "/series/{metricType}",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [AllowedValues(
                        ToolCall,
                        UserAgent,
                        DocumentRead,
                        SectionRead,
                        SnippetRead,
                        AgentTokenUsage,
                        AgentCostUsd
                    )]
                        string metricType,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery] MetricSeriesGroup groupBy,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? users,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? tools,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? libraries,
                    [FromServices] GetMetricSeriesHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        metricType,
                        window,
                        groupBy,
                        users,
                        tools,
                        libraries,
                        ct
                    )
            )
            .WithName("GetMetricSeries")
            .Produces<MetricSeriesPoint[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Bucketed metric series.")
            .WithDescription(
                "Returns a bucketed metric_value series for the window, optionally grouped and filtered."
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/series/{metricType}/two-dimensional
        group
            .MapGet(
                "/series/{metricType}/two-dimensional",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [AllowedValues(
                        ToolCall,
                        UserAgent,
                        DocumentRead,
                        SectionRead,
                        SnippetRead,
                        AgentTokenUsage,
                        AgentCostUsd
                    )]
                        string metricType,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery] MetricSeriesGroup primaryGroupBy,
                    [FromQuery] MetricSeriesGroup secondaryGroupBy,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? users,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? tools,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? libraries,
                    [FromServices] GetMetricTwoDimensionalSeriesHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        metricType,
                        window,
                        primaryGroupBy,
                        secondaryGroupBy,
                        users,
                        tools,
                        libraries,
                        ct
                    )
            )
            .WithName("GetMetricTwoDimensionalSeries")
            .Produces<MetricTwoDimensionalSeriesPoint[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Bucketed metric series grouped by two dimensions.")
            .WithDescription(
                "Returns a bucketed metric_value series for the window, grouped by two distinct dimensions and optionally filtered."
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/percentiles/{metricType}
        group
            .MapGet(
                "/percentiles/{metricType}",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [AllowedValues(ReviewDuration, ReviewTokens, ReviewCost)] string metricType,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery, MaxLength(MaxIdLength)] string? repositoryId,
                    [FromQuery, MaxLength(MaxFacetLength)] string? facet,
                    [FromServices] GetMetricPercentilesHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, metricType, window, repositoryId, facet, ct)
            )
            .WithName("GetMetricPercentiles")
            .Produces<MetricPercentilePoint[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Per-bucket p50/p95/p99.")
            .WithDescription("Returns per-bucket p50/p95/p99 for a histogram metric.")
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/scatter/{metricType}
        group
            .MapGet(
                "/scatter/{metricType}",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [AllowedValues(ReviewDuration, ReviewTokens, ReviewCost)] string metricType,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery, MaxLength(MaxIdLength)] string? repositoryId,
                    [FromQuery, MaxLength(MaxFacetLength)] string? facet,
                    [FromQuery, Range(1, 2000)] int? limit,
                    [FromServices] GetMetricScatterHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, metricType, window, repositoryId, facet, limit, ct)
            )
            .WithName("GetMetricScatter")
            .Produces<MetricScatterPoint[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Duration-vs-tokens scatter sample.")
            .WithDescription("Returns recent raw samples for a duration-vs-tokens scatter.")
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/leaderboard
        group
            .MapGet(
                "/leaderboard",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery, MaxLength(MaxIdLength)] string? library,
                    [FromQuery, Range(1, 100)] int? top,
                    [FromServices] GetMetricLeaderboardHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, window, library, top, ct)
            )
            .WithName("GetMetricLeaderboard")
            .Produces<MetricLeaderboardItem[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Top-N read paths (sections + snippets).")
            .WithDescription(
                "Returns the top-N most-read document paths across section and snippet reads."
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/leaderboard/sections
        group
            .MapGet(
                "/leaderboard/sections",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery, AllowedValues("section", "code")] string kind,
                    [FromQuery, MaxLength(MaxIdLength)] string? library,
                    [FromQuery, Range(1, 100)] int? top,
                    [FromServices] GetMetricSectionLeaderboardHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, window, kind, library, top, ct)
            )
            .WithName("GetMetricSectionLeaderboard")
            .Produces<MetricLeaderboardItem[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Top-N read sections or snippets (path + heading), one kind at a time.")
            .WithDescription(
                """
                Returns the top-N most-read sections or code snippets (per `kind`), keyed by document path + heading.

                Distinct sections within the same document rank separately.

                Section and snippet reads are never combined in one query: a prose section and a code snippet under the same heading share an identical (path, heading) pair.

                Combining kinds would merge their counts.
                """
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/reviews/volume
        group
            .MapGet(
                "/reviews/volume",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? repositoryIds,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? authorLogins,
                    [FromQuery] CodeReviewRequestOrigin? origin,
                    [FromQuery] ReviewVolumeGroup groupBy,
                    [FromServices] GetReviewVolumeHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        window,
                        repositoryIds,
                        authorLogins,
                        origin,
                        groupBy,
                        ct
                    )
            )
            .WithName("GetReviewVolume")
            .Produces<ReviewVolumePoint[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Bucketed review volume.")
            .WithDescription(
                "Returns bucketed code-review volume, optionally grouped by repo/author/origin."
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/reviews/findings
        group
            .MapGet(
                "/reviews/findings",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? repositoryIds,
                    [FromQuery, MaxLength(MaxFilterValues)] string[]? authorLogins,
                    [FromQuery] ReviewFindingsGroup groupBy,
                    [FromServices] GetReviewFindingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, window, repositoryIds, authorLogins, groupBy, ct)
            )
            .WithName("GetReviewFindings")
            .Produces<ReviewFindingsPoint[]>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Bucketed finding-severity sums.")
            .WithDescription(
                "Returns bucketed finding-severity sums, optionally grouped by repo/author."
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/reviews/findings/list
        group
            .MapGet(
                "/reviews/findings/list",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromQuery] FindingSeverity severity,
                    [FromQuery, MaxLength(MaxIdLength)] string? cursor,
                    [FromQuery, Range(1, 100)] int? limit,
                    [FromServices] ListFindingReviewsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, window, severity, cursor, limit, ct)
            )
            .WithName("ListFindingReviews")
            .Produces<FindingReviewListResponse>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Findings drill-down list, newest-first and cursor-paginated.")
            .WithDescription(
                """
                Returns review groups (deduplicated to the latest attempt per pull request/agent
                session) with at least one finding of the given severity in the window. Backs the
                Critical/Major findings stat-card slideover — not cached, since it's a paginated,
                click-to-open list rather than a polled dashboard tile.
                """
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/overview
        group
            .MapGet(
                "/overview",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [FromQuery]
                    [AllowedValues("15m", "30m", "1h", "4h", "12h", "24h", "72h", "7d", "14d", "30d")]
                        string? window,
                    [FromServices] GetMetricsOverviewHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, window, ct)
            )
            .WithName("GetMetricsOverview")
            .Produces<MetricsOverview>()
            .Produces<MetricsEndpointError>(StatusCodes.Status400BadRequest)
            .WithSummary("Overview stat-card numbers.")
            .WithDescription("Returns the headline overview numbers for the window.")
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/metrics/filter-options
        group
            .MapGet(
                "/filter-options",
                static (
                    [MaxLength(MaxIdLength)] string orgId,
                    [FromServices] GetMetricsFilterOptionsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, ct)
            )
            .WithName("GetMetricsFilterOptions")
            .Produces<MetricsFilterOptions>()
            .WithSummary("Dashboard filter options.")
            .WithDescription(
                "Returns distinct users, tools, repositories, and authors for the dashboard filters."
            )
            .RequireActiveOrganization();
    }
}
