using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Metrics;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresMetricsQueryStore" /> — exercises the raw
/// <c>date_bin</c>/<c>percentile_cont</c>/<c>ANY</c>/jsonb SQL against a real Postgres, including
/// the highest-value guarantee: single-organization scoping with no cross-org leakage.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/MetricsQueryStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class MetricsQueryStoreIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly MetricQueryRange OneHourRange = new(
        Now.AddHours(-1),
        Now.AddMinutes(1),
        TimeSpan.FromMinutes(5)
    );
    private static readonly MetricQueryRange OneDayRange = new(
        Now.AddDays(-1),
        Now.AddMinutes(1),
        TimeSpan.FromHours(1)
    );

    [Test]
    public async Task GetReviewCosts_GroupsByAuthorWithinOrganizationAndWindow()
    {
        await SeedMetricsAsync(
            ReviewCost("org_cost_a", "review_1", "alice", 0.012),
            ReviewCost("org_cost_a", "review_2", "alice", 0.003),
            ReviewCost("org_cost_a", "review_3", "bob", 0.005),
            ReviewCost("org_cost_b", "review_4", "alice", 8.0)
        );

        var store = new PostgresMetricsQueryStore(_context);
        var result = await store.GetReviewCostMetricsAsync(
            "org_cost_a",
            OneDayRange,
            CancellationToken.None
        );

        await Assert.That(result.Reviews.Count).IsEqualTo(3);
        await Assert.That(result.HasMoreReviews).IsFalse();
        await Assert
            .That(result.Reviews.Single(point => point.ReviewId == "review_1").ViewToken)
            .IsEqualTo("token_1");
        await Assert
            .That(
                result.ByAuthor.Where(point => point.SeriesKey == "alice").Sum(point => point.Value)
            )
            .IsEqualTo(0.015d)
            .Within(0.000001d);
        await Assert
            .That(result.ByAuthor.Sum(point => point.Value))
            .IsEqualTo(0.020d)
            .Within(0.000001d);
    }

    [Test]
    public async Task GetReviewCosts_FlagsAndOrdersTruncatedReviewSamples()
    {
        await SeedMetricsAsync(
            Enumerable
                .Range(0, 2001)
                .Select(index => ReviewCost("org_cost_cap", $"review_{index:D4}", "alice", 0.001))
                .ToArray()
        );

        var store = new PostgresMetricsQueryStore(_context);
        var result = await store.GetReviewCostMetricsAsync(
            "org_cost_cap",
            OneDayRange,
            CancellationToken.None
        );

        await Assert.That(result.HasMoreReviews).IsTrue();
        await Assert.That(result.Reviews.Count).IsEqualTo(2000);
        await Assert.That(result.Reviews[0].ReviewId).IsEqualTo("review_2000");
        await Assert.That(result.Reviews[^1].ReviewId).IsEqualTo("review_0001");
        await Assert
            .That(result.ByAuthor.Sum(point => point.Value))
            .IsEqualTo(2.001d)
            .Within(0.000001d);
    }

    [Test]
    public async Task MetricAndReviewQueries_UseInclusiveStartExclusiveEndAndOrganizationBounds()
    {
        var organizationId = "org_range_bounds";
        var range = new MetricQueryRange(
            Now.AddMinutes(-10),
            Now.AddMinutes(10),
            TimeSpan.FromMinutes(5)
        );
        var beforeStart = range.StartUtc.AddMilliseconds(-1);
        var beforeEnd = range.EndExclusiveUtc.AddMilliseconds(-1);
        var atEnd = range.EndExclusiveUtc;

        var metricEvents = new[]
        {
            At(ToolCall(organizationId, "range"), beforeStart),
            At(ToolCall(organizationId, "range"), range.StartUtc),
            At(ToolCall(organizationId, "range"), beforeEnd),
            At(ToolCall(organizationId, "range"), atEnd),
            At(ToolCall("org_range_bounds_other", "range"), range.StartUtc),
            At(ReviewCost(organizationId, "range_cost_start", "range-author", 1), range.StartUtc),
            At(ReviewCost(organizationId, "range_cost_end", "range-author", 2), beforeEnd),
            At(ReviewCost(organizationId, "range_cost_before", "range-author", 4), beforeStart),
            At(ReviewCost(organizationId, "range_cost_at_end", "range-author", 8), atEnd),
            At(
                ReviewCost("org_range_bounds_other", "range_cost_other", "range-author", 16),
                range.StartUtc
            ),
            At(
                AgentMetric(
                    organizationId,
                    "zeeq_agent_token_usage",
                    2,
                    "range-model",
                    "range-user"
                ),
                range.StartUtc
            ),
            At(
                AgentMetric(
                    organizationId,
                    "zeeq_agent_token_usage",
                    3,
                    "range-model",
                    "range-user"
                ),
                beforeEnd
            ),
            At(
                AgentMetric(organizationId, "zeeq_agent_token_usage", 5, "outside", "range-user"),
                atEnd
            ),
            At(
                AgentMetric(
                    "org_range_bounds_other",
                    "zeeq_agent_token_usage",
                    7,
                    "outside",
                    "range-user"
                ),
                range.StartUtc
            ),
            At(ScatterMetric(organizationId, 20, 200), range.StartUtc),
            At(ScatterMetric(organizationId, 40, 400), beforeEnd),
            At(ScatterMetric(organizationId, 1, 10), beforeStart),
            At(ScatterMetric(organizationId, 80, 800), atEnd),
            At(ScatterMetric("org_range_bounds_other", 100, 1000), range.StartUtc),
            At(LeaderboardMetric(organizationId, "zeeq_document_read_counter", 1), range.StartUtc),
            At(LeaderboardMetric(organizationId, "zeeq_document_read_counter", 1), beforeEnd),
            At(LeaderboardMetric(organizationId, "zeeq_document_read_counter", 4), beforeStart),
            At(LeaderboardMetric(organizationId, "zeeq_document_read_counter", 8), atEnd),
            At(
                LeaderboardMetric("org_range_bounds_other", "zeeq_document_read_counter", 16),
                range.StartUtc
            ),
            At(
                LeaderboardMetric(organizationId, "zeeq_section_read_counter", 1, "Range heading"),
                range.StartUtc
            ),
            At(
                LeaderboardMetric(organizationId, "zeeq_section_read_counter", 1, "Range heading"),
                beforeEnd
            ),
            At(
                LeaderboardMetric(
                    organizationId,
                    "zeeq_section_read_counter",
                    4,
                    "Outside heading"
                ),
                beforeStart
            ),
            At(
                LeaderboardMetric(organizationId, "zeeq_section_read_counter", 8, "End heading"),
                atEnd
            ),
            At(
                LeaderboardMetric(
                    "org_range_bounds_other",
                    "zeeq_section_read_counter",
                    16,
                    "Range heading"
                ),
                range.StartUtc
            ),
            At(
                PromptGet(organizationId, "zeeq-app", "/range/prompt.md", "range-prompt"),
                range.StartUtc
            ),
            At(
                PromptGet(organizationId, "zeeq-app", "/range/prompt.md", "range-prompt"),
                beforeEnd
            ),
            At(PromptGet(organizationId, "zeeq-app", "/range/outside.md", "outside"), beforeStart),
            At(PromptGet(organizationId, "zeeq-app", "/range/end.md", "end"), atEnd),
            At(
                PromptGet("org_range_bounds_other", "zeeq-app", "/range/prompt.md", "range-prompt"),
                range.StartUtc
            ),
        };
        await SeedMetricsAsync(metricEvents);

        var startReview = Review(organizationId, critical: 1, createdAtUtc: range.StartUtc);
        startReview.ReviewGroupId = "range_start_group";
        var lastReview = Review(organizationId, critical: 2, createdAtUtc: beforeEnd);
        lastReview.ReviewGroupId = "range_end_group";
        var beforeReview = Review(organizationId, critical: 4, createdAtUtc: beforeStart);
        beforeReview.ReviewGroupId = "range_before_group";
        var atEndReview = Review(organizationId, critical: 8, createdAtUtc: atEnd);
        atEndReview.ReviewGroupId = "range_at_end_group";
        var otherOrganizationReview = Review(
            "org_range_bounds_other",
            critical: 16,
            createdAtUtc: range.StartUtc
        );
        otherOrganizationReview.ReviewGroupId = "range_other_org_group";
        await SeedReviewsAsync(
            startReview,
            lastReview,
            beforeReview,
            atEndReview,
            otherOrganizationReview
        );

        var store = new PostgresMetricsQueryStore(_context);

        var costs = await store.GetReviewCostMetricsAsync(
            organizationId,
            range,
            CancellationToken.None
        );
        await Assert.That(costs.Reviews.Count).IsEqualTo(2);
        await Assert.That(costs.ByAuthor.Sum(point => point.Value)).IsEqualTo(3d);

        var series = await store.GetSeriesAsync(
            organizationId,
            "zeeq_tool_call_counter",
            range,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(),
            CancellationToken.None
        );
        await Assert.That(series.Sum(point => point.Value)).IsEqualTo(2d);

        var twoDimensional = await store.GetTwoDimensionalSeriesAsync(
            organizationId,
            "zeeq_agent_token_usage",
            range,
            MetricSeriesGroup.Model,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(),
            CancellationToken.None
        );
        await Assert.That(twoDimensional.Sum(point => point.Value)).IsEqualTo(5d);

        var percentiles = await store.GetPercentileSeriesAsync(
            organizationId,
            "zeeq_review_duration_ms",
            range,
            repositoryId: null,
            facet: null,
            CancellationToken.None
        );
        await Assert.That(percentiles.Sum(point => point.P50)).IsEqualTo(60d);

        var scatter = await store.GetScatterSampleAsync(
            organizationId,
            "zeeq_review_duration_ms",
            range,
            repositoryId: null,
            facet: null,
            limit: 10,
            CancellationToken.None
        );
        await Assert.That(scatter.Count).IsEqualTo(2);

        var leaderboard = await store.GetLeaderboardAsync(
            organizationId,
            ["zeeq_document_read_counter"],
            range,
            library: null,
            top: 10,
            CancellationToken.None
        );
        await Assert.That(leaderboard.Single().Item).IsEqualTo("/range/document.md");
        await Assert.That(leaderboard.Single().Value).IsEqualTo(2d);

        var sectionLeaderboard = await store.GetSectionLeaderboardAsync(
            organizationId,
            ["zeeq_section_read_counter"],
            range,
            library: null,
            top: 10,
            CancellationToken.None
        );
        await Assert.That(sectionLeaderboard.Single().Item).IsEqualTo("Range heading");
        await Assert.That(sectionLeaderboard.Single().Value).IsEqualTo(2d);

        var promptLeaderboard = await store.GetPromptLeaderboardAsync(
            organizationId,
            range,
            users: null,
            library: null,
            top: 10,
            CancellationToken.None
        );
        await Assert.That(promptLeaderboard.Single().Item).IsEqualTo("zeeq-app:/range/prompt.md");
        await Assert.That(promptLeaderboard.Single().Value).IsEqualTo(2d);

        var reviewVolume = await store.GetReviewVolumeSeriesAsync(
            organizationId,
            range,
            repositoryIds: null,
            authorLogins: null,
            requestOrigin: null,
            groupBy: ReviewVolumeGroup.Origin,
            CancellationToken.None
        );
        await Assert.That(reviewVolume.Sum(point => point.Count)).IsEqualTo(2L);

        var reviewFindings = await store.GetReviewFindingsSeriesAsync(
            organizationId,
            range,
            repositoryIds: null,
            authorLogins: null,
            groupBy: ReviewFindingsGroup.Origin,
            CancellationToken.None
        );
        await Assert.That(reviewFindings.Sum(point => point.Critical)).IsEqualTo(3L);

        var overview = await store.GetOverviewAsync(organizationId, range, CancellationToken.None);
        await Assert.That(overview.ToolCalls).IsEqualTo(2d);
        await Assert.That(overview.Reviews).IsEqualTo(2L);
        await Assert.That(overview.CriticalFindings).IsEqualTo(3L);

        var findingGroups = await store.ListFindingReviewGroupsAsync(
            organizationId,
            range,
            FindingSeverity.Critical,
            cursorCreatedAtUtc: null,
            cursorId: null,
            limit: 10,
            CancellationToken.None
        );
        await Assert.That(findingGroups.Count).IsEqualTo(2);
        await Assert.That(findingGroups.Sum(group => group.GroupCriticalFindings)).IsEqualTo(3L);
    }

    [Test]
    public async Task ListFindingReviewGroups_UsesOnlyAttemptsInsideSelectedRange()
    {
        var organizationId = "org_range_attempts";
        var range = new MetricQueryRange(
            Now.AddMinutes(-10),
            Now.AddMinutes(10),
            TimeSpan.FromMinutes(5)
        );
        var before = Review(
            organizationId,
            critical: 100,
            createdAtUtc: range.StartUtc.AddMilliseconds(-1)
        );
        before.ReviewGroupId = "range_attempts_group";

        var firstInside = Review(
            organizationId,
            critical: 2,
            createdAtUtc: range.StartUtc.AddMinutes(2)
        );
        firstInside.ReviewGroupId = "range_attempts_group";
        firstInside.Title = "first attempt inside range";

        var latestInside = Review(
            organizationId,
            critical: 3,
            createdAtUtc: range.StartUtc.AddMinutes(3)
        );
        latestInside.ReviewGroupId = "range_attempts_group";
        latestInside.Title = "latest attempt inside range";

        var after = Review(
            organizationId,
            critical: 200,
            createdAtUtc: range.EndExclusiveUtc.AddMilliseconds(1)
        );
        after.ReviewGroupId = "range_attempts_group";
        after.Title = "later attempt outside range";

        await SeedReviewsAsync(before, firstInside, latestInside, after);

        var store = new PostgresMetricsQueryStore(_context);
        var groups = await store.ListFindingReviewGroupsAsync(
            organizationId,
            range,
            FindingSeverity.Critical,
            cursorCreatedAtUtc: null,
            cursorId: null,
            limit: 10,
            CancellationToken.None
        );

        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].ReviewId).IsEqualTo(latestInside.Id);
        await Assert.That(groups[0].Title).IsEqualTo("latest attempt inside range");
        await Assert.That(groups[0].GroupCriticalFindings).IsEqualTo(5L);
    }

    [Test]
    public async Task GetSeries_ScopesToOrganization_NoCrossOrgLeakage()
    {
        // The highest-value test in the feature: an org's series must never include another org's rows.
        await SeedMetricsAsync(
            ToolCall("org_q_a", "search_documents"),
            ToolCall("org_q_a", "search_documents"),
            ToolCall("org_q_b", "search_documents")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            "org_q_a",
            "zeeq_tool_call_counter",
            OneHourRange,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(),
            CancellationToken.None
        );

        var total = series.Sum(point => point.Value);
        await Assert.That(total).IsEqualTo(2d);
    }

    [Test]
    public async Task GetSeries_GroupByTool_BucketsAndSums()
    {
        await SeedMetricsAsync(
            ToolCall("org_q_group", "search_documents"),
            ToolCall("org_q_group", "search_documents"),
            ToolCall("org_q_group", "read_document_by_path")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            "org_q_group",
            "zeeq_tool_call_counter",
            OneHourRange,
            MetricSeriesGroup.Tool,
            new MetricSeriesFilters(),
            CancellationToken.None
        );

        await Assert
            .That(series.Where(p => p.SeriesKey == "search_documents").Sum(p => p.Value))
            .IsEqualTo(2d);
        await Assert
            .That(series.Where(p => p.SeriesKey == "read_document_by_path").Sum(p => p.Value))
            .IsEqualTo(1d);
    }

    [Test]
    public async Task GetSeries_GroupByModel_ReadsAgentTelemetryTags()
    {
        // Agent token usage is emitted as histogram samples, but the dashboard uses the series
        // endpoint to show summed usage by model over time. The model dimension lives in jsonb
        // tags, not in a first-class metric_events column.
        await SeedMetricsAsync(
            AgentMetric("org_q_model", "zeeq_agent_token_usage", 100, "gpt-5-codex"),
            AgentMetric("org_q_model", "zeeq_agent_token_usage", 50, "gpt-5-codex"),
            AgentMetric("org_q_model", "zeeq_agent_token_usage", 25, "gpt-5-mini")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            "org_q_model",
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.Model,
            new MetricSeriesFilters(),
            CancellationToken.None
        );

        await Assert
            .That(series.Where(p => p.SeriesKey == "gpt-5-codex").Sum(p => p.Value))
            .IsEqualTo(150d);
        await Assert
            .That(series.Where(p => p.SeriesKey == "gpt-5-mini").Sum(p => p.Value))
            .IsEqualTo(25d);
    }

    [Test]
    public async Task GetTwoDimensionalSeries_GroupByModelAndUser_BucketsAndSums()
    {
        await SeedMetricsAsync(
            AgentMetric(
                "org_q_model_user",
                "zeeq_agent_token_usage",
                100,
                "gpt-5-codex",
                "a@x.com"
            ),
            AgentMetric("org_q_model_user", "zeeq_agent_token_usage", 50, "gpt-5-codex", "a@x.com"),
            AgentMetric("org_q_model_user", "zeeq_agent_token_usage", 25, "gpt-5-codex", "b@x.com"),
            AgentMetric("org_q_model_user", "zeeq_agent_token_usage", 10, "gpt-5-mini", "a@x.com")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetTwoDimensionalSeriesAsync(
            "org_q_model_user",
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.Model,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(),
            CancellationToken.None
        );
        var actualOrder = series
            .Select(p => (p.Bucket, p.PrimarySeriesKey, p.SecondarySeriesKey))
            .ToArray();
        var expectedOrder = series
            .OrderBy(p => p.Bucket)
            .ThenBy(p => p.PrimarySeriesKey, StringComparer.Ordinal)
            .ThenBy(p => p.SecondarySeriesKey, StringComparer.Ordinal)
            .Select(p => (p.Bucket, p.PrimarySeriesKey, p.SecondarySeriesKey))
            .ToArray();

        await Assert.That(actualOrder.SequenceEqual(expectedOrder)).IsTrue();
        await Assert
            .That(
                series
                    .Where(p =>
                        p.PrimarySeriesKey == "gpt-5-codex" && p.SecondarySeriesKey == "a@x.com"
                    )
                    .Sum(p => p.Value)
            )
            .IsEqualTo(150d);
        await Assert
            .That(
                series
                    .Where(p =>
                        p.PrimarySeriesKey == "gpt-5-codex" && p.SecondarySeriesKey == "b@x.com"
                    )
                    .Sum(p => p.Value)
            )
            .IsEqualTo(25d);
        await Assert
            .That(
                series
                    .Where(p =>
                        p.PrimarySeriesKey == "gpt-5-mini" && p.SecondarySeriesKey == "a@x.com"
                    )
                    .Sum(p => p.Value)
            )
            .IsEqualTo(10d);
    }

    [Test]
    public async Task GetSeries_GroupByUser_ResolvesEmailAliasesToCanonicalMemberEmail()
    {
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal@example.com";
                    alias.NormalizedValue = "personal@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member@company.com";

        await SeedMetricsAsync(
            AgentMetric(org, "zeeq_agent_token_usage", 100, "gpt-5-codex", "personal@example.com")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(),
            CancellationToken.None
        );

        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].SeriesKey).IsEqualTo("member@company.com");
        await Assert.That(series[0].Value).IsEqualTo(100d);
    }

    [Test]
    public async Task GetSeries_GroupByUser_DoesNotResolveDisabledEmailAliases()
    {
        var now = DateTimeOffset.UtcNow;
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "disabled-personal@example.com";
                    alias.NormalizedValue = "disabled-personal@example.com";
                    alias.DisabledAtUtc = now;
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member@company.com";

        await SeedMetricsAsync(
            AgentMetric(
                org,
                "zeeq_agent_token_usage",
                100,
                "gpt-5-codex",
                "disabled-personal@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(),
            CancellationToken.None
        );

        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].SeriesKey).IsEqualTo("disabled-personal@example.com");
    }

    [Test]
    public async Task GetSeries_GroupByUser_FallsBackToTelemetryEmailWhenCanonicalUserHasNoEmail()
    {
        // Regression test: User.Email is nullable (e.g. an IdP login that never surfaced an
        // email claim). When the alias join resolves to such a user, the series key must
        // fall back to the telemetry-reported email on the metric row itself -- never to
        // the bare internal user_id, which is meaningless to anyone reading the dashboard.
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal-no-email@example.com";
                    alias.NormalizedValue = "personal-no-email@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = null;

        await SeedMetricsAsync(
            AgentMetric(
                org,
                "zeeq_agent_token_usage",
                100,
                "gpt-5-codex",
                "personal-no-email@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(),
            CancellationToken.None
        );

        await Assert.That(series.Count).IsEqualTo(1);
        await Assert.That(series[0].SeriesKey).IsEqualTo("personal-no-email@example.com");
        await Assert.That(series[0].SeriesKey).IsNotEqualTo(seed.Owner.Id);
    }

    [Test]
    public async Task GetSeries_UserFilter_UsesCanonicalAliasKey()
    {
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal-filter@example.com";
                    alias.NormalizedValue = "personal-filter@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member-filter@company.com";

        await SeedMetricsAsync(
            AgentMetric(
                org,
                "zeeq_agent_token_usage",
                100,
                "gpt-5-codex",
                "personal-filter@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var canonicalSeries = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(Users: ["member-filter@company.com"]),
            CancellationToken.None
        );
        var rawAliasSeries = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(Users: ["personal-filter@example.com"]),
            CancellationToken.None
        );

        await Assert.That(canonicalSeries.Sum(p => p.Value)).IsEqualTo(100d);
        await Assert.That(rawAliasSeries.Sum(p => p.Value)).IsEqualTo(0d);
    }

    [Test]
    public async Task GetTwoDimensionalSeries_UserFilter_UsesCanonicalAliasKey()
    {
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal-2d@example.com";
                    alias.NormalizedValue = "personal-2d@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member-2d@company.com";

        await SeedMetricsAsync(
            AgentMetric(org, "zeeq_agent_token_usage", 75, "gpt-5-codex", "personal-2d@example.com")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var canonicalSeries = await store.GetTwoDimensionalSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.Model,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(Users: ["member-2d@company.com"]),
            CancellationToken.None
        );
        var rawAliasSeries = await store.GetTwoDimensionalSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.Model,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(Users: ["personal-2d@example.com"]),
            CancellationToken.None
        );

        await Assert.That(canonicalSeries.Sum(p => p.Value)).IsEqualTo(75d);
        await Assert
            .That(canonicalSeries.Single().SecondarySeriesKey)
            .IsEqualTo("member-2d@company.com");
        await Assert.That(rawAliasSeries.Sum(p => p.Value)).IsEqualTo(0d);
    }

    [Test]
    public async Task GetSeries_UserFilter_UsesMultiSelect()
    {
        await SeedMetricsAsync(
            ToolCall("org_q_filter", "search_documents", user: "a@x.com"),
            ToolCall("org_q_filter", "search_documents", user: "b@x.com"),
            ToolCall("org_q_filter", "search_documents", user: "c@x.com")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            "org_q_filter",
            "zeeq_tool_call_counter",
            OneHourRange,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(Users: ["a@x.com", "b@x.com"]),
            CancellationToken.None
        );

        await Assert.That(series.Sum(p => p.Value)).IsEqualTo(2d);
    }

    [Test]
    public async Task GetSeries_UserFilter_ResolvesMixOfAliasedAndDirectCanonicalKeys()
    {
        // Regression test for ResolveNormalizedUserFilterKeysAsync's UNION: one filter key
        // resolves via an active alias (personal-mixed -> member-mixed), the other filter key has
        // no alias at all and must match its own raw telemetry email directly. Both branches must
        // fire in the same query.
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal-mixed@example.com";
                    alias.NormalizedValue = "personal-mixed@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member-mixed@company.com";

        await SeedMetricsAsync(
            AgentMetric(
                org,
                "zeeq_agent_token_usage",
                40,
                "gpt-5-codex",
                "personal-mixed@example.com"
            ),
            AgentMetric(
                org,
                "zeeq_agent_token_usage",
                15,
                "gpt-5-codex",
                "direct-mixed@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            OneHourRange,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(
                Users: ["member-mixed@company.com", "direct-mixed@example.com"]
            ),
            CancellationToken.None
        );

        await Assert.That(series.Sum(p => p.Value)).IsEqualTo(55d);
    }

    [Test]
    public async Task GetSeries_UserFilter_NonMatchingCanonicalKeyReturnsNoRows()
    {
        // A non-empty original filter that resolves to zero raw emails must exclude every row,
        // not fall back to unfiltered — the resolver's empty result and "no filter requested" are
        // different things.
        await SeedMetricsAsync(ToolCall("org_q_nomatch", "search_documents", user: "a@x.com"));

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            "org_q_nomatch",
            "zeeq_tool_call_counter",
            OneHourRange,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(Users: ["nobody@nowhere.example"]),
            CancellationToken.None
        );

        await Assert.That(series.Sum(p => p.Value)).IsEqualTo(0d);
    }

    [Test]
    public async Task GetPromptLeaderboard_UserFilter_ResolvesCanonicalAliasKey()
    {
        // GetPromptLeaderboardAsync previously shared the same join-based filter as GetSeriesAsync
        // but had no test covering alias resolution specifically (only direct raw-email filtering,
        // see GetPromptLeaderboard_RanksDocumentPathsAndFiltersByUserAndLibrary). This closes that
        // gap for the now-join-free implementation.
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal-prompt@example.com";
                    alias.NormalizedValue = "personal-prompt@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member-prompt@company.com";

        await SeedMetricsAsync(
            PromptGet(
                org,
                "backend",
                "/backend/shared.md",
                "shared-skill",
                "personal-prompt@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var canonicalItems = await store.GetPromptLeaderboardAsync(
            org,
            OneHourRange,
            users: ["member-prompt@company.com"],
            library: null,
            top: 10,
            CancellationToken.None
        );
        var rawAliasItems = await store.GetPromptLeaderboardAsync(
            org,
            OneHourRange,
            users: ["personal-prompt@example.com"],
            library: null,
            top: 10,
            CancellationToken.None
        );

        await Assert.That(canonicalItems.Count).IsEqualTo(1);
        await Assert.That(canonicalItems[0].Value).IsEqualTo(1d);
        await Assert.That(rawAliasItems.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPercentiles_ComputesInterpolatedPercentilesPerBucket()
    {
        // percentile_cont interpolates: for [10,20,30,40,50] p50=30, p95=48, p99=49.6.
        double[] durations = [10, 20, 30, 40, 50];
        await SeedMetricsAsync([
            .. durations.Select(value =>
                Metric("org_q_pct", "zeeq_review_duration_ms", value, facet: "security")
            ),
        ]);

        var store = new PostgresMetricsQueryStore(_context);
        var points = await store.GetPercentileSeriesAsync(
            "org_q_pct",
            "zeeq_review_duration_ms",
            OneHourRange,
            repositoryId: null,
            facet: null,
            CancellationToken.None
        );

        await Assert.That(points.Count).IsEqualTo(1);
        await Assert.That(points[0].P50).IsEqualTo(30d).Within(0.001);
        await Assert.That(points[0].P95).IsEqualTo(48d).Within(0.001);
        await Assert.That(points[0].P99).IsEqualTo(49.6d).Within(0.001);
    }

    [Test]
    public async Task GetLeaderboard_RanksPathsAcrossSectionAndSnippetTypes()
    {
        await SeedMetricsAsync(
            Read("org_q_lb", "zeeq_section_read_counter", "/a.md"),
            Read("org_q_lb", "zeeq_section_read_counter", "/a.md"),
            Read("org_q_lb", "zeeq_snippet_read_counter", "/a.md"),
            Read("org_q_lb", "zeeq_section_read_counter", "/b.md")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var items = await store.GetLeaderboardAsync(
            "org_q_lb",
            ["zeeq_section_read_counter", "zeeq_snippet_read_counter"],
            OneHourRange,
            library: null,
            top: 10,
            CancellationToken.None
        );

        await Assert.That(items.Count).IsEqualTo(2);
        await Assert.That(items[0].Item).IsEqualTo("/a.md");
        await Assert.That(items[0].Value).IsEqualTo(3d);
        await Assert.That(items[1].Item).IsEqualTo("/b.md");
        await Assert.That(items[1].Value).IsEqualTo(1d);
    }

    [Test]
    public async Task GetSectionLeaderboard_RanksSectionsWithinTheSamePathSeparately()
    {
        // Two distinct sections in the same document (/a.md) must rank as separate items —
        // this is exactly the gap GetLeaderboardAsync (path-only) can't see. The displayed
        // item is heading-only, but two different documents that happen to reuse the same
        // heading text (/a.md and /d.md both have "Intro") must still count separately —
        // path stays in the GROUP BY even though it's dropped from the SELECT.
        await SeedMetricsAsync(
            Read("org_q_slb", "zeeq_section_read_counter", "/a.md", "Intro"),
            Read("org_q_slb", "zeeq_section_read_counter", "/a.md", "Intro"),
            Read("org_q_slb", "zeeq_snippet_read_counter", "/a.md", "Setup"),
            Read("org_q_slb", "zeeq_section_read_counter", "/b.md", "Overview"),
            Read("org_q_slb", "zeeq_section_read_counter", "/d.md", "Intro"),
            // No heading (e.g. a document read) must be excluded entirely.
            Read("org_q_slb", "zeeq_section_read_counter", "/c.md")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var items = await store.GetSectionLeaderboardAsync(
            "org_q_slb",
            ["zeeq_section_read_counter", "zeeq_snippet_read_counter"],
            OneHourRange,
            library: null,
            top: 10,
            CancellationToken.None
        );

        await Assert.That(items.Count).IsEqualTo(4);
        await Assert.That(items[0].Item).IsEqualTo("Intro");
        await Assert.That(items[0].Value).IsEqualTo(2d);
        await Assert.That(items.Select(i => i.Item)).Contains("Setup");
        await Assert.That(items.Select(i => i.Item)).Contains("Overview");
        // Both "Intro" rows survive as distinct entries (2 from /a.md, 1 from /d.md) rather
        // than merging into a single value=3 row.
        await Assert.That(items.Count(i => i.Item == "Intro")).IsEqualTo(2);
    }

    [Test]
    public async Task GetLeaderboard_UserFilter_ResolvesCanonicalAliasKey()
    {
        // Closes the gap GetLeaderboardAsync previously had no user dimension at all -- same
        // alias-resolution semantics as GetPromptLeaderboard_UserFilter_ResolvesCanonicalAliasKey:
        // the canonical (post-alias) email matches, the raw pre-alias email filtered directly does
        // not (its resolved identity is the alias owner's email, not itself).
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal-lb@example.com";
                    alias.NormalizedValue = "personal-lb@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member-lb@company.com";

        await SeedMetricsAsync(
            Read(org, "zeeq_section_read_counter", "/a.md", user: "personal-lb@example.com"),
            Read(org, "zeeq_section_read_counter", "/b.md", user: "someone-else@example.com")
        );

        var store = new PostgresMetricsQueryStore(_context);
        var canonicalItems = await store.GetLeaderboardAsync(
            org,
            ["zeeq_section_read_counter"],
            OneHourRange,
            library: null,
            top: 10,
            CancellationToken.None,
            users: ["member-lb@company.com"]
        );
        var rawAliasItems = await store.GetLeaderboardAsync(
            org,
            ["zeeq_section_read_counter"],
            OneHourRange,
            library: null,
            top: 10,
            CancellationToken.None,
            users: ["personal-lb@example.com"]
        );

        await Assert.That(canonicalItems.Count).IsEqualTo(1);
        await Assert.That(canonicalItems[0].Item).IsEqualTo("/a.md");
        await Assert.That(rawAliasItems.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetSectionLeaderboard_UserFilter_ResolvesCanonicalAliasKey()
    {
        await TestBiome
            .Start(_context, out var seed)
            .Under(seed.Owner)
            .AddAliases(
                out _,
                alias =>
                {
                    alias.DisplayValue = "personal-slb@example.com";
                    alias.NormalizedValue = "personal-slb@example.com";
                }
            )
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member-slb@company.com";

        await SeedMetricsAsync(
            Read(
                org,
                "zeeq_section_read_counter",
                "/a.md",
                "Intro",
                user: "personal-slb@example.com"
            ),
            Read(
                org,
                "zeeq_section_read_counter",
                "/b.md",
                "Overview",
                user: "someone-else@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var canonicalItems = await store.GetSectionLeaderboardAsync(
            org,
            ["zeeq_section_read_counter"],
            OneHourRange,
            library: null,
            top: 10,
            CancellationToken.None,
            users: ["member-slb@company.com"]
        );
        var rawAliasItems = await store.GetSectionLeaderboardAsync(
            org,
            ["zeeq_section_read_counter"],
            OneHourRange,
            library: null,
            top: 10,
            CancellationToken.None,
            users: ["personal-slb@example.com"]
        );

        await Assert.That(canonicalItems.Count).IsEqualTo(1);
        await Assert.That(canonicalItems[0].Item).IsEqualTo("Intro");
        await Assert.That(rawAliasItems.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPromptLeaderboard_RanksDocumentPathsAndFiltersByUserAndLibrary()
    {
        await SeedMetricsAsync(
            PromptGet(
                "org_q_prompt_lb",
                "backend",
                "/backend/shared.md",
                "shared-skill",
                "alice@example.com"
            ),
            PromptGet(
                "org_q_prompt_lb",
                "backend",
                "/backend/shared.md",
                "shared-skill",
                "alice@example.com"
            ),
            PromptGet(
                "org_q_prompt_lb",
                "backend",
                "/backend/shared.md",
                "shared-skill",
                "alice@example.com"
            ),
            PromptGet(
                "org_q_prompt_lb",
                "frontend",
                "/frontend/shared.md",
                "shared-skill",
                "alice@example.com"
            ),
            PromptGet(
                "org_q_prompt_lb",
                "backend",
                "/backend/other.md",
                "other-skill",
                "alice@example.com"
            ),
            PromptGet(
                "org_q_prompt_lb",
                "backend",
                "/backend/other.md",
                "other-skill",
                "alice@example.com"
            ),
            PromptGet(
                "org_q_prompt_lb",
                "backend",
                "/backend/shared.md",
                "shared-skill",
                "bob@example.com"
            ),
            PromptGet(
                "org_q_prompt_lb_other",
                "backend",
                "/backend/shared.md",
                "shared-skill",
                "alice@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var globalItems = await store.GetPromptLeaderboardAsync(
            "org_q_prompt_lb",
            OneHourRange,
            users: ["alice@example.com"],
            library: null,
            top: 10,
            CancellationToken.None
        );

        await Assert.That(globalItems.Count).IsEqualTo(3);
        await Assert.That(globalItems[0].Item).IsEqualTo("backend:/backend/shared.md");
        await Assert.That(globalItems[0].Library).IsEqualTo("backend");
        await Assert.That(globalItems[0].Value).IsEqualTo(3d);
        await Assert.That(globalItems[1].Item).IsEqualTo("backend:/backend/other.md");
        await Assert.That(globalItems[1].Value).IsEqualTo(2d);
        await Assert.That(globalItems[2].Item).IsEqualTo("frontend:/frontend/shared.md");
        await Assert.That(globalItems[2].Value).IsEqualTo(1d);

        var backendItems = await store.GetPromptLeaderboardAsync(
            "org_q_prompt_lb",
            OneHourRange,
            users: ["alice@example.com"],
            library: "backend",
            top: 10,
            CancellationToken.None
        );

        await Assert.That(backendItems.Count).IsEqualTo(2);
        await Assert.That(backendItems[0].Item).IsEqualTo("backend:/backend/shared.md");
        await Assert.That(backendItems[0].Library).IsEqualTo("backend");
        await Assert.That(backendItems[0].Value).IsEqualTo(3d);
    }

    [Test]
    public async Task GetReviewVolume_GroupByOrigin_MatchesSeededRecords()
    {
        await SeedReviewsAsync(
            Review("org_q_rv", origin: CodeReviewRequestOrigin.Agent),
            Review("org_q_rv", origin: CodeReviewRequestOrigin.Agent),
            Review("org_q_rv", origin: CodeReviewRequestOrigin.RepositoryWebhook)
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetReviewVolumeSeriesAsync(
            "org_q_rv",
            OneHourRange,
            repositoryIds: null,
            authorLogins: null,
            requestOrigin: null,
            ReviewVolumeGroup.Origin,
            CancellationToken.None
        );

        await Assert
            .That(
                series
                    .Where(p => p.SeriesKey == nameof(CodeReviewRequestOrigin.Agent))
                    .Sum(p => p.Count)
            )
            .IsEqualTo(2L);
        await Assert
            .That(
                series
                    .Where(p => p.SeriesKey == nameof(CodeReviewRequestOrigin.RepositoryWebhook))
                    .Sum(p => p.Count)
            )
            .IsEqualTo(1L);
    }

    [Test]
    public async Task GetReviewFindings_SumsSeverityColumns()
    {
        await SeedReviewsAsync(
            Review("org_q_rf", critical: 2, major: 1),
            Review("org_q_rf", critical: 3, major: 4)
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetReviewFindingsSeriesAsync(
            "org_q_rf",
            OneHourRange,
            repositoryIds: null,
            authorLogins: null,
            ReviewFindingsGroup.Repo,
            CancellationToken.None
        );

        await Assert.That(series.Sum(p => p.Critical)).IsEqualTo(5L);
        await Assert.That(series.Sum(p => p.Major)).IsEqualTo(5L);
    }

    [Test]
    public async Task ListFindingReviewGroups_ResolvesInternalAuthorUserIdToEmail()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member@company.com";

        await SeedReviewsAsync(
            Review(
                org,
                origin: CodeReviewRequestOrigin.Agent,
                critical: 1,
                authorLogin: seed.Owner.Id
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var groups = await store.ListFindingReviewGroupsAsync(
            org,
            OneHourRange,
            FindingSeverity.Critical,
            cursorCreatedAtUtc: null,
            cursorId: null,
            limit: 10,
            CancellationToken.None
        );

        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].AuthorLogin).IsEqualTo("member@company.com");
    }

    [Test]
    public async Task ListFindingReviewGroups_DoesNotResolveWebhookAuthorThatLooksLikeUserId()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member@company.com";

        await SeedReviewsAsync(
            Review(
                org,
                origin: CodeReviewRequestOrigin.RepositoryWebhook,
                critical: 1,
                authorLogin: seed.Owner.Id
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var groups = await store.ListFindingReviewGroupsAsync(
            org,
            OneHourRange,
            FindingSeverity.Critical,
            cursorCreatedAtUtc: null,
            cursorId: null,
            limit: 10,
            CancellationToken.None
        );

        await Assert.That(groups.Count).IsEqualTo(1);
        await Assert.That(groups[0].AuthorLogin).IsEqualTo(seed.Owner.Id);
    }

    [Test]
    public async Task GetReviewVolume_GroupByRepo_ResolvesDisplayNameEvenWhenSoftDeleted()
    {
        // The repository row still exists (just soft-deleted), so review rows whose repo mapping was
        // later removed still resolve to a readable name rather than the raw id. code_review_repositories
        // has an org FK, so seed a real organization graph first.
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var org = seed.Organization.Id;

        await SeedRepositoriesAsync(Repository(org, "repo_q", "acme/widgets", disabled: true));
        await SeedReviewsAsync(Review(org), Review(org));

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetReviewVolumeSeriesAsync(
            org,
            OneHourRange,
            repositoryIds: null,
            authorLogins: null,
            requestOrigin: null,
            ReviewVolumeGroup.Repo,
            CancellationToken.None
        );

        await Assert.That(series.All(p => p.SeriesKey == "acme/widgets")).IsTrue();
        await Assert.That(series.Sum(p => p.Count)).IsEqualTo(2L);
    }

    [Test]
    public async Task GetOverview_ReturnsHeadlineNumbers()
    {
        await SeedMetricsAsync(
            ToolCall("org_q_ov", "search_documents"),
            ToolCall("org_q_ov", "search_documents"),
            Read("org_q_ov", "zeeq_document_read_counter", "/d.md"),
            Metric("org_q_ov", "zeeq_review_duration_ms", 100)
        );
        await SeedReviewsAsync(Review("org_q_ov", critical: 1, major: 2));

        var store = new PostgresMetricsQueryStore(_context);
        var overview = await store.GetOverviewAsync(
            "org_q_ov",
            OneHourRange,
            CancellationToken.None
        );

        await Assert.That(overview.ToolCalls).IsEqualTo(2d);
        await Assert.That(overview.KnowledgeReads).IsEqualTo(1d);
        await Assert.That(overview.Reviews).IsEqualTo(1L);
        await Assert.That(overview.CriticalFindings).IsEqualTo(1L);
        await Assert.That(overview.MajorFindings).IsEqualTo(2L);
        await Assert.That(overview.P95ReviewDurationMs).IsEqualTo(100d).Within(0.001);
    }

    [Test]
    public async Task GetFilterOptions_ReturnsDistinctValuesIncludingSoftDeletedRepos()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var org = seed.Organization.Id;

        await SeedMetricsAsync(
            ToolCall(org, "search_documents", user: "alice@x.com"),
            ToolCall(org, "read_document", user: "bob@x.com"),
            ToolCall(org, "search_documents", user: "alice@x.com")
        );
        seed.Owner.Email = "member@company.com";
        await SeedRepositoriesAsync(Repository(org, "repo_deleted", "acme/old", disabled: true));
        await SeedReviewsAsync(
            Review(org),
            Review(org, origin: CodeReviewRequestOrigin.Agent, authorLogin: seed.Owner.Id)
        );

        var store = new PostgresMetricsQueryStore(_context);
        var options = await store.GetFilterOptionsAsync(org, CancellationToken.None);

        await Assert.That(options.Users.Count).IsEqualTo(2);
        await Assert.That(options.Users.Contains("alice@x.com")).IsTrue();
        await Assert.That(options.Tools.Contains("search_documents")).IsTrue();
        await Assert.That(options.Authors.Contains("octocat")).IsTrue();
        await Assert.That(options.Authors.Contains("member@company.com")).IsTrue();
        await Assert
            .That(options.Repositories.Any(repo => repo.DisplayName == "acme/old"))
            .IsTrue();
    }

    private async Task SeedMetricsAsync(params MetricEvent[] events)
    {
        _context.MetricEvents.AddRange(events);
        await _context.SaveChangesAsync();
    }

    private async Task SeedReviewsAsync(params CodeReviewRecord[] reviews)
    {
        _context.CodeReviewRecords.AddRange(reviews);
        await _context.SaveChangesAsync();
    }

    private async Task SeedRepositoriesAsync(params CodeRepository[] repositories)
    {
        _context.Set<CodeRepository>().AddRange(repositories);
        await _context.SaveChangesAsync();
    }

    private static CodeRepository Repository(
        string org,
        string id,
        string displayName,
        bool disabled = false
    ) =>
        new()
        {
            Id = id,
            OrganizationId = org,
            Provider = "github",
            OwnerQualifiedName = displayName,
            DisplayName = displayName,
            Enabled = !disabled,
            DisabledAtUtc = disabled ? Now : null,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };

    private static MetricEvent Metric(
        string org,
        string metricType,
        double value,
        string? facet = null
    ) =>
        new()
        {
            OrganizationId = org,
            MetricType = metricType,
            MetricValue = value,
            Facet = facet,
            CreatedAtUtc = Now,
        };

    private static MetricEvent ScatterMetric(string org, double value, double tokens) =>
        new()
        {
            OrganizationId = org,
            MetricType = "zeeq_review_duration_ms",
            MetricValue = value,
            Tags = new()
            {
                ["tokens"] = tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            CreatedAtUtc = Now,
        };

    private static MetricEvent LeaderboardMetric(
        string org,
        string metricType,
        double value,
        string? heading = null
    ) =>
        new()
        {
            OrganizationId = org,
            MetricType = metricType,
            MetricValue = value,
            Library = "zeeq-app",
            Tags = heading is null
                ? new() { ["path"] = "/range/document.md" }
                : new() { ["path"] = "/range/section.md", ["heading"] = heading },
            CreatedAtUtc = Now,
        };

    private static MetricEvent At(MetricEvent metric, DateTimeOffset createdAtUtc)
    {
        metric.CreatedAtUtc = createdAtUtc;
        return metric;
    }

    private static MetricEvent AgentMetric(
        string org,
        string metricType,
        double value,
        string model,
        string? user = null
    ) =>
        new()
        {
            OrganizationId = org,
            MetricType = metricType,
            MetricValue = value,
            UserEmail = user,
            Tags = new() { ["model"] = model },
            CreatedAtUtc = Now,
        };

    private static MetricEvent ToolCall(string org, string tool, string? user = null) =>
        new()
        {
            OrganizationId = org,
            MetricType = "zeeq_tool_call_counter",
            MetricValue = 1,
            ToolName = tool,
            UserEmail = user,
            CreatedAtUtc = Now,
        };

    private static MetricEvent ReviewCost(string org, string id, string author, double cost) =>
        new()
        {
            OrganizationId = org,
            MetricType = "zeeq_review_cost_usd",
            MetricValue = cost,
            Tags = new()
            {
                ["review_id"] = id,
                ["view_token"] = $"token_{id[^1]}",
                ["author_login"] = author,
            },
            CreatedAtUtc = Now,
        };

    private static MetricEvent Read(
        string org,
        string metricType,
        string path,
        string? heading = null,
        string? user = null
    ) =>
        new()
        {
            OrganizationId = org,
            MetricType = metricType,
            MetricValue = 1,
            Library = "zeeq-app",
            UserEmail = user,
            Tags = heading is null
                ? new() { ["path"] = path }
                : new() { ["path"] = path, ["heading"] = heading },
            CreatedAtUtc = Now,
        };

    private static MetricEvent PromptGet(
        string org,
        string library,
        string documentPath,
        string promptName,
        string? user = null
    ) =>
        new()
        {
            OrganizationId = org,
            MetricType = "zeeq_prompt_get_counter",
            MetricValue = 1,
            Library = library,
            UserEmail = user,
            Tags = new() { ["document_path"] = documentPath, ["prompt_name"] = promptName },
            CreatedAtUtc = Now,
        };

    private static CodeReviewRecord Review(
        string org,
        CodeReviewRequestOrigin origin = CodeReviewRequestOrigin.RepositoryWebhook,
        int critical = 0,
        int major = 0,
        string authorLogin = "octocat",
        DateTimeOffset? createdAtUtc = null
    ) =>
        new()
        {
            Id = $"cr_{Guid.CreateVersion7():N}",
            CreatedAtUtc = createdAtUtc ?? Now,
            UpdatedAtUtc = Now,
            OrganizationId = org,
            RepositoryId = "repo_q",
            OwnerQualifiedRepoName = "acme/repo",
            Branch = "main",
            Title = "Test review",
            AuthorLogin = authorLogin,
            Status = CodeReviewStatus.Completed,
            RequestOrigin = origin,
            CriticalFindings = critical,
            MajorFindings = major,
        };
}
