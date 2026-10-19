using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
            null,
            null,
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
    public async Task GetMetricSeries_AbsoluteDates_AreForwardedAndKeyedByExactRange()
    {
        var store = new FakeMetricsQueryStore();
        var handler = new GetMetricSeriesHandler(store, new MetricsTestHybridCache());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddDays(-2);
        var startText = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        for (var i = 0; i < 2; i++)
        {
            await handler.HandleAsync(
                "org_a",
                "zeeq_tool_call_counter",
                null,
                startText,
                endText,
                MetricSeriesGroup.None,
                null,
                null,
                null,
                CancellationToken.None
            );
        }

        await Assert.That(store.SeriesRanges.Count).IsEqualTo(1);
        await Assert
            .That(store.SeriesRanges[0].StartUtc)
            .IsEqualTo(new DateTimeOffset(start, TimeOnly.MinValue, TimeSpan.Zero));
        await Assert
            .That(store.SeriesRanges[0].EndExclusiveUtc)
            .IsLessThanOrEqualTo(
                new DateTimeOffset(today.AddDays(1), TimeOnly.MinValue, TimeSpan.Zero)
            );
        await Assert.That(store.SeriesRanges[0].Bucket).IsEqualTo(TimeSpan.FromHours(2));

        await handler.HandleAsync(
            "org_a",
            "zeeq_tool_call_counter",
            null,
            today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endText,
            MetricSeriesGroup.None,
            null,
            null,
            null,
            CancellationToken.None
        );

        await Assert.That(store.SeriesRanges.Count).IsEqualTo(2);
        await Assert
            .That(store.SeriesRanges[1].StartUtc)
            .IsEqualTo(new DateTimeOffset(today, TimeOnly.MinValue, TimeSpan.Zero));
        await Assert.That(store.SeriesRanges[1].Bucket).IsEqualTo(TimeSpan.FromHours(1));
    }

    [Test]
    public async Task ListFindingReviews_UsesBoundedPageSizeAndLegacyCursorForPageTwo()
    {
        var first = FindingGroup("review_3", DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = FindingGroup("review_2", DateTimeOffset.UtcNow.AddMinutes(-2));
        var third = FindingGroup("review_1", DateTimeOffset.UtcNow.AddMinutes(-3));
        var store = new FakeMetricsQueryStore { FindingReviewGroups = [first, second, third] };
        var handler = new ListFindingReviewsHandler(store, CreateLinkFactory());

        var firstPage = await handler.HandleAsync(
            "org_a",
            "24h",
            null,
            null,
            FindingSeverity.Critical,
            cursor: null,
            limit: 2,
            CancellationToken.None
        );

        var firstPageResponse = firstPage.Result as Ok<FindingReviewListResponse>;
        await Assert.That(firstPageResponse).IsNotNull();
        await Assert.That(firstPageResponse!.Value!.Items.Count).IsEqualTo(2);
        await Assert.That(firstPageResponse.Value.Items[1].ReviewId).IsEqualTo(second.ReviewId);
        await Assert.That(firstPageResponse.Value.NextCursor).IsNotNull();
        await Assert.That(store.FindingReviewQueries[0].Limit).IsEqualTo(3);

        var cursor = firstPageResponse.Value.NextCursor!;
        await Assert
            .That(ReviewFindingsListCursor.TryDecode(cursor, out var cursorDate, out var cursorId))
            .IsTrue();
        await Assert.That(cursorDate).IsEqualTo(second.ReviewCreatedAtUtc);
        await Assert.That(cursorId).IsEqualTo(second.ReviewId);

        var secondPage = await handler.HandleAsync(
            "org_a",
            "24h",
            null,
            null,
            FindingSeverity.Critical,
            cursor,
            2,
            CancellationToken.None
        );

        var secondPageResponse = secondPage.Result as Ok<FindingReviewListResponse>;
        await Assert.That(secondPageResponse).IsNotNull();
        await Assert.That(secondPageResponse!.Value!.Items.Count).IsEqualTo(1);
        await Assert.That(secondPageResponse.Value.Items[0].ReviewId).IsEqualTo(third.ReviewId);
        await Assert.That(secondPageResponse.Value.NextCursor).IsNull();
        await Assert.That(store.FindingReviewQueries[1].CursorCreatedAtUtc).IsEqualTo(cursorDate);
        await Assert.That(store.FindingReviewQueries[1].CursorId).IsEqualTo(cursorId);
        await Assert.That(store.FindingReviewQueries[1].Limit).IsEqualTo(3);
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
            null,
            null,
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<MetricsEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("unknown_metric_type");
    }

    private static CodeReviewRequestLinkFactory CreateLinkFactory() =>
        new(
            Options.Create(new AppSettings()),
            new CodeReviewRequestTokenProtector(
                new CodeReviewSettings { ReviewRequestLinkEncryptionKey = "unit-test-key" }
            )
        );

    private static FindingReviewGroup FindingGroup(string reviewId, DateTimeOffset createdAtUtc) =>
        new(
            reviewId,
            createdAtUtc,
            "Test title",
            "acme/repo",
            1,
            "octocat",
            CodeReviewRequestOrigin.RepositoryWebhook,
            null,
            null,
            1,
            0
        );

    [Test]
    public async Task GetMetricsOverview_InvalidWindow_Returns400()
    {
        var handler = new GetMetricsOverviewHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "bogus",
            null,
            null,
            CancellationToken.None
        );

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

        var result = await handler.HandleAsync("org_a", "24h", null, null, CancellationToken.None);

        var ok = result.Result as Ok<MetricsOverview>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.ToolCalls).IsEqualTo(10d);
        await Assert.That(ok.Value.Reviews).IsEqualTo(2L);
    }

    [Test]
    public async Task GetPromptLeaderboard_ValidRequest_ReturnsStoreData()
    {
        var store = new FakeMetricsQueryStore
        {
            PromptLeaderboard = [new("dotnet-csharp-best-practices", "backend", 2)],
        };
        var handler = new GetPromptLeaderboardHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync(
            "org_a",
            "1h",
            null,
            null,
            ["alice@example.com"],
            "backend",
            10,
            CancellationToken.None
        );

        var ok = result.Result as Ok<MetricLeaderboardItem[]>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Length).IsEqualTo(1);
        await Assert.That(ok.Value[0].Item).IsEqualTo("dotnet-csharp-best-practices");
        await Assert.That(store.PromptLeaderboardOrganizations).Contains("org_a");
        await Assert.That(store.PromptLeaderboardUsers).IsEquivalentTo(["alice@example.com"]);
    }

    [Test]
    public async Task GetMetricLeaderboard_UsersFilter_ForwardsToStore()
    {
        var store = new FakeMetricsQueryStore
        {
            Leaderboard = [new("/backend/shared.md", "backend", 3)],
        };
        var handler = new GetMetricLeaderboardHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync(
            "org_a",
            "1h",
            null,
            null,
            "backend",
            10,
            ["alice@example.com"],
            CancellationToken.None
        );

        var ok = result.Result as Ok<MetricLeaderboardItem[]>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Length).IsEqualTo(1);
        await Assert.That(store.LeaderboardUsers).IsEquivalentTo(["alice@example.com"]);
    }

    [Test]
    public async Task GetMetricSectionLeaderboard_UsersFilter_ForwardsToStore()
    {
        var store = new FakeMetricsQueryStore { SectionLeaderboard = [new("Intro", "backend", 2)] };
        var handler = new GetMetricSectionLeaderboardHandler(store, new MetricsTestHybridCache());

        var result = await handler.HandleAsync(
            "org_a",
            "1h",
            null,
            null,
            "section",
            "backend",
            10,
            ["alice@example.com"],
            CancellationToken.None
        );

        var ok = result.Result as Ok<MetricLeaderboardItem[]>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Length).IsEqualTo(1);
        await Assert.That(store.SectionLeaderboardUsers).IsEquivalentTo(["alice@example.com"]);
    }

    [Test]
    public async Task GetPromptLeaderboard_InvalidWindow_Returns400()
    {
        var handler = new GetPromptLeaderboardHandler(
            new FakeMetricsQueryStore(),
            new MetricsTestHybridCache()
        );

        var result = await handler.HandleAsync(
            "org_a",
            "bogus",
            null,
            null,
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
        public Task<ReviewCostMetrics> GetReviewCostMetricsAsync(
            string organizationId,
            MetricQueryRange range,
            CancellationToken cancellationToken
        ) => Task.FromResult<ReviewCostMetrics>(new([], [], false));

        public MetricSeriesPoint[] Series { get; init; } = [];
        public MetricTwoDimensionalSeriesPoint[] TwoDimensionalSeries { get; init; } = [];
        public MetricLeaderboardItem[] PromptLeaderboard { get; init; } = [];
        public MetricLeaderboardItem[] Leaderboard { get; init; } = [];
        public MetricLeaderboardItem[] SectionLeaderboard { get; init; } = [];
        public MetricsOverview Overview { get; init; } = new(0, 0, 0, 0, 0, 0);
        public List<string> SeriesOrganizations { get; } = [];
        public List<MetricQueryRange> SeriesRanges { get; } = [];
        public List<string> TwoDimensionalSeriesOrganizations { get; } = [];
        public List<string> PromptLeaderboardOrganizations { get; } = [];
        public string[]? PromptLeaderboardUsers { get; private set; }
        public string[]? LeaderboardUsers { get; private set; }
        public string[]? SectionLeaderboardUsers { get; private set; }

        public Task<IReadOnlyList<MetricSeriesPoint>> GetSeriesAsync(
            string organizationId,
            string metricType,
            MetricQueryRange range,
            MetricSeriesGroup groupBy,
            MetricSeriesFilters filters,
            CancellationToken cancellationToken
        )
        {
            SeriesOrganizations.Add(organizationId);
            SeriesRanges.Add(range);
            return Task.FromResult<IReadOnlyList<MetricSeriesPoint>>(Series);
        }

        public Task<IReadOnlyList<MetricTwoDimensionalSeriesPoint>> GetTwoDimensionalSeriesAsync(
            string organizationId,
            string metricType,
            MetricQueryRange range,
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
            MetricQueryRange range,
            string? repositoryId,
            string? facet,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<MetricPercentilePoint>>([]);

        public Task<IReadOnlyList<MetricScatterPoint>> GetScatterSampleAsync(
            string organizationId,
            string metricType,
            MetricQueryRange range,
            string? repositoryId,
            string? facet,
            int limit,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<MetricScatterPoint>>([]);

        public Task<IReadOnlyList<MetricLeaderboardItem>> GetLeaderboardAsync(
            string organizationId,
            string[] metricTypes,
            MetricQueryRange range,
            string? library,
            int top,
            CancellationToken cancellationToken,
            string[]? users = null
        )
        {
            LeaderboardUsers = users;
            return Task.FromResult<IReadOnlyList<MetricLeaderboardItem>>(Leaderboard);
        }

        public Task<IReadOnlyList<MetricLeaderboardItem>> GetSectionLeaderboardAsync(
            string organizationId,
            string[] metricTypes,
            MetricQueryRange range,
            string? library,
            int top,
            CancellationToken cancellationToken,
            string[]? users = null
        )
        {
            SectionLeaderboardUsers = users;
            return Task.FromResult<IReadOnlyList<MetricLeaderboardItem>>(SectionLeaderboard);
        }

        public Task<IReadOnlyList<MetricLeaderboardItem>> GetPromptLeaderboardAsync(
            string organizationId,
            MetricQueryRange range,
            string[]? users,
            string? library,
            int top,
            CancellationToken cancellationToken
        )
        {
            PromptLeaderboardOrganizations.Add(organizationId);
            PromptLeaderboardUsers = users;
            return Task.FromResult<IReadOnlyList<MetricLeaderboardItem>>(PromptLeaderboard);
        }

        public Task<IReadOnlyList<ReviewVolumePoint>> GetReviewVolumeSeriesAsync(
            string organizationId,
            MetricQueryRange range,
            string[]? repositoryIds,
            string[]? authorLogins,
            CodeReviewRequestOrigin? requestOrigin,
            ReviewVolumeGroup groupBy,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ReviewVolumePoint>>([]);

        public Task<IReadOnlyList<ReviewFindingsPoint>> GetReviewFindingsSeriesAsync(
            string organizationId,
            MetricQueryRange range,
            string[]? repositoryIds,
            string[]? authorLogins,
            ReviewFindingsGroup groupBy,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<ReviewFindingsPoint>>([]);

        public Task<MetricsOverview> GetOverviewAsync(
            string organizationId,
            MetricQueryRange range,
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
        public List<FindingReviewQuery> FindingReviewQueries { get; } = [];

        public Task<IReadOnlyList<FindingReviewGroup>> ListFindingReviewGroupsAsync(
            string organizationId,
            MetricQueryRange range,
            FindingSeverity severity,
            DateTimeOffset? cursorCreatedAtUtc,
            string? cursorId,
            int limit,
            CancellationToken cancellationToken
        )
        {
            FindingReviewQueries.Add(
                new(organizationId, range, severity, cursorCreatedAtUtc, cursorId, limit)
            );
            IReadOnlyList<FindingReviewGroup> afterCursor = FindingReviewGroups;
            if (cursorCreatedAtUtc is { } afterDate && cursorId is not null)
            {
                afterCursor = FindingReviewGroups
                    .Where(group =>
                        group.ReviewCreatedAtUtc < afterDate
                        || (
                            group.ReviewCreatedAtUtc == afterDate
                            && string.CompareOrdinal(group.ReviewId, cursorId) < 0
                        )
                    )
                    .ToArray();
            }
            return Task.FromResult<IReadOnlyList<FindingReviewGroup>>(
                afterCursor.Take(limit).ToArray()
            );
        }

        public sealed record FindingReviewQuery(
            string OrganizationId,
            MetricQueryRange Range,
            FindingSeverity Severity,
            DateTimeOffset? CursorCreatedAtUtc,
            string? CursorId,
            int Limit
        );
    }
}
