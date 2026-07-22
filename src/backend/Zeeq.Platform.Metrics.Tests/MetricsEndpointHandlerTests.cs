using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics.Tests;

/// <summary>
/// Handler-level tests for the metrics read endpoints — window/metric-type validation (400 paths),
/// store forwarding, and per-organization cache-key isolation. The store's SQL/cross-org behavior
/// is covered separately by the query-store integration tests.
/// </summary>
public sealed class MetricsEndpointHandlerTests
{
    [Test]
    public async Task GetMetricSeries_UnknownMetricType_Returns400()
    {
        var handler = new GetMetricSeriesHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "not_a_real_metric",
            "1h",
            MetricSeriesGroup.None,
            null,
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("unknown_metric_type");
    }

    [Test]
    public async Task GetMetricSeries_InvalidWindow_Returns400()
    {
        var handler = new GetMetricSeriesHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_tool_call_counter",
            "not_a_window",
            MetricSeriesGroup.None,
            null,
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_window");
    }

    [Test]
    public async Task GetMetricSeries_ValidRequest_ReturnsStoreData()
    {
        var store = new FakeMetricsQueryStore
        {
            Series = [new(DateTimeOffset.UtcNow, "search_documents", 3)],
        };
        var handler = new GetMetricSeriesHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_tool_call_counter",
            "1h",
            MetricSeriesGroup.Tool,
            null,
            null,
            null,
            CancellationToken.None
        );

        var ok = result.Result as Ok<MetricSeriesPoint[]>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Length).IsEqualTo(1);
        await Assert.That(ok.Value[0].Value).IsEqualTo(3d);
        await Assert.That(store.SeriesOrganizations).Contains("org_a");
    }

    [Test]
    public async Task GetMetricSeries_AgentHistogramMetric_ReturnsSummedSeries()
    {
        // Token and cost samples are histogram instruments at write time, but the dashboard's
        // aggregate panels need their SUM(metric_value) over the selected window.
        var store = new FakeMetricsQueryStore
        {
            Series = [new(DateTimeOffset.UtcNow, "gpt-5-codex", 250)],
        };
        var handler = new GetMetricSeriesHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_agent_token_usage",
            "1h",
            MetricSeriesGroup.Model,
            null,
            null,
            null,
            CancellationToken.None
        );

        var ok = result.Result as Ok<MetricSeriesPoint[]>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value![0].SeriesKey).IsEqualTo("gpt-5-codex");
        await Assert.That(ok.Value[0].Value).IsEqualTo(250d);
    }

    [Test]
    public async Task GetMetricTwoDimensionalSeries_ValidRequest_ReturnsStoreData()
    {
        var store = new FakeMetricsQueryStore
        {
            TwoDimensionalSeries = [new(DateTimeOffset.UtcNow, "gpt-5-codex", "alice@x.com", 250)],
        };
        var handler = new GetMetricTwoDimensionalSeriesHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_agent_token_usage",
            "1h",
            MetricSeriesGroup.Model,
            MetricSeriesGroup.User,
            null,
            null,
            null,
            CancellationToken.None
        );

        var ok = result.Result as Ok<MetricTwoDimensionalSeriesPoint[]>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Length).IsEqualTo(1);
        await Assert.That(ok.Value[0].PrimarySeriesKey).IsEqualTo("gpt-5-codex");
        await Assert.That(ok.Value[0].SecondarySeriesKey).IsEqualTo("alice@x.com");
        await Assert.That(store.TwoDimensionalSeriesOrganizations).Contains("org_a");
    }

    [Test]
    public async Task GetMetricTwoDimensionalSeries_NoneDimension_Returns400()
    {
        var handler = new GetMetricTwoDimensionalSeriesHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_agent_token_usage",
            "1h",
            MetricSeriesGroup.Model,
            MetricSeriesGroup.None,
            null,
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_group_by");
    }

    [Test]
    public async Task GetMetricTwoDimensionalSeries_UndefinedDimension_Returns400()
    {
        var handler = new GetMetricTwoDimensionalSeriesHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_agent_token_usage",
            "1h",
            MetricSeriesGroup.Model,
            (MetricSeriesGroup)999,
            null,
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_group_by");
    }

    [Test]
    public async Task GetMetricTwoDimensionalSeries_DuplicateDimension_Returns400()
    {
        var handler = new GetMetricTwoDimensionalSeriesHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_agent_token_usage",
            "1h",
            MetricSeriesGroup.Model,
            MetricSeriesGroup.Model,
            null,
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_group_by");
    }

    [Test]
    public async Task GetMetricSeries_DifferentOrganizations_QueryEachIndependently()
    {
        // Guards that the cache key includes the organization: two orgs must each hit the store,
        // never sharing a cached result.
        var store = new FakeMetricsQueryStore();
        var cache = new MetricsTestHybridCache();
        var handler = new GetMetricSeriesHandler(store, cache);

        await handler.HandleAsync(
            "org_a",
            "zeeq_tool_call_counter",
            "1h",
            MetricSeriesGroup.None,
            null,
            null,
            null,
            CancellationToken.None
        );
        await handler.HandleAsync(
            "org_b",
            "zeeq_tool_call_counter",
            "1h",
            MetricSeriesGroup.None,
            null,
            null,
            null,
            CancellationToken.None
        );

        await Assert.That(store.SeriesOrganizations).Contains("org_a");
        await Assert.That(store.SeriesOrganizations).Contains("org_b");
    }

    [Test]
    public async Task GetMetricPercentiles_UnknownHistogramType_Returns400()
    {
        var handler = new GetMetricPercentilesHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "zeeq_tool_call_counter", // a counter, not a histogram
            "1h",
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("unknown_metric_type");
    }

    [Test]
    public async Task GetMetricsOverview_InvalidWindow_Returns400()
    {
        var handler = new GetMetricsOverviewHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync("org_a", "bogus", CancellationToken.None);

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_window");
    }

    [Test]
    public async Task GetMetricsOverview_ValidRequest_ReturnsOverview()
    {
        var store = new FakeMetricsQueryStore
        {
            Overview = new MetricsOverview(10, 5, 2, 1, 0, 250),
        };
        var handler = new GetMetricsOverviewHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync("org_a", "24h", CancellationToken.None);

        var ok = result.Result as Ok<MetricsOverview>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.ToolCalls).IsEqualTo(10d);
        await Assert.That(ok.Value.Reviews).IsEqualTo(2L);
    }

    [Test]
    public async Task GetReviewVolume_InvalidWindow_Returns400()
    {
        var handler = new GetReviewVolumeHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "bogus",
            null,
            null,
            null,
            ReviewVolumeGroup.Repo,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_window");
    }

    [Test]
    public async Task GetFilterOptions_ForwardsOrganizationAndReturnsOptions()
    {
        var store = new FakeMetricsQueryStore
        {
            FilterOptions = new(
                ["alice@x.com"],
                ["search_sections"],
                [new MetricsRepositoryOption("repo_1", "acme/repo")],
                ["octocat"]
            ),
        };
        var handler = new GetMetricsFilterOptionsHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync("org_a", CancellationToken.None);

        await Assert.That(result.Value).IsNotNull();
        await Assert.That(result.Value!.Users.Count).IsEqualTo(1);
        await Assert.That(result.Value!.Repositories.Count).IsEqualTo(1);
        await Assert.That(store.FilterOptionsOrganizations.Contains("org_a")).IsTrue();
    }

    private sealed class FakeMetricsQueryStore : IMetricsQueryStore
    {
        public MetricSeriesPoint[] Series { get; init; } = [];
        public MetricTwoDimensionalSeriesPoint[] TwoDimensionalSeries { get; init; } = [];
        public MetricsOverview Overview { get; init; } = new(0, 0, 0, 0, 0, 0);
        public List<string> SeriesOrganizations { get; } = [];
        public List<string> TwoDimensionalSeriesOrganizations { get; } = [];

        public Task<IReadOnlyList<MetricSeriesPoint>> GetSeriesAsync(
            string organizationId,
            string metricType,
            MetricWindow window,
            MetricSeriesGroup groupBy,
            MetricSeriesFilters filters,
            CancellationToken cancellationToken
        )
        {
            SeriesOrganizations.Add(organizationId);
            return Task.FromResult<IReadOnlyList<MetricSeriesPoint>>(Series);
        }

        public Task<IReadOnlyList<MetricTwoDimensionalSeriesPoint>> GetTwoDimensionalSeriesAsync(
            string organizationId,
            string metricType,
            MetricWindow window,
            MetricSeriesGroup primaryGroupBy,
            MetricSeriesGroup secondaryGroupBy,
            MetricSeriesFilters filters,
            CancellationToken cancellationToken
        )
        {
            TwoDimensionalSeriesOrganizations.Add(organizationId);
            return Task.FromResult<IReadOnlyList<MetricTwoDimensionalSeriesPoint>>(
                TwoDimensionalSeries
            );
        }

        public Task<IReadOnlyList<MetricPercentilePoint>> GetPercentileSeriesAsync(
            string organizationId,
            string metricType,
            MetricWindow window,
            string? repositoryId,
            string? facet,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<MetricPercentilePoint>>([]);

        public Task<IReadOnlyList<MetricScatterPoint>> GetScatterSampleAsync(
            string organizationId,
            string metricType,
            MetricWindow window,
            string? repositoryId,
            string? facet,
            int limit,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<MetricScatterPoint>>([]);

        public Task<IReadOnlyList<MetricLeaderboardItem>> GetLeaderboardAsync(
            string organizationId,
            string[] metricTypes,
            MetricWindow window,
            string? library,
            int top,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<MetricLeaderboardItem>>([]);

        public Task<IReadOnlyList<MetricLeaderboardItem>> GetSectionLeaderboardAsync(
            string organizationId,
            string[] metricTypes,
            MetricWindow window,
            string? library,
            int top,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<MetricLeaderboardItem>>([]);

        public Task<IReadOnlyList<ReviewVolumePoint>> GetReviewVolumeSeriesAsync(
            string organizationId,
            MetricWindow window,
            string[]? repositoryIds,
            string[]? authorLogins,
            CodeReviewRequestOrigin? requestOrigin,
            ReviewVolumeGroup groupBy,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ReviewVolumePoint>>([]);

        public Task<IReadOnlyList<ReviewFindingsPoint>> GetReviewFindingsSeriesAsync(
            string organizationId,
            MetricWindow window,
            string[]? repositoryIds,
            string[]? authorLogins,
            ReviewFindingsGroup groupBy,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ReviewFindingsPoint>>([]);

        public Task<MetricsOverview> GetOverviewAsync(
            string organizationId,
            MetricWindow window,
            CancellationToken cancellationToken
        ) => Task.FromResult(Overview);

        public MetricsFilterOptions FilterOptions { get; init; } = new([], [], [], []);
        public List<string> FilterOptionsOrganizations { get; } = [];

        public Task<MetricsFilterOptions> GetFilterOptionsAsync(
            string organizationId,
            CancellationToken cancellationToken
        )
        {
            FilterOptionsOrganizations.Add(organizationId);
            return Task.FromResult(FilterOptions);
        }

        public IReadOnlyList<FindingReviewGroup> FindingReviewGroups { get; init; } = [];

        public Task<IReadOnlyList<FindingReviewGroup>> ListFindingReviewGroupsAsync(
            string organizationId,
            MetricWindow window,
            FindingSeverity severity,
            DateTimeOffset? cursorCreatedAtUtc,
            string? cursorId,
            int limit,
            CancellationToken cancellationToken
        ) => Task.FromResult(FindingReviewGroups);
    }
}
