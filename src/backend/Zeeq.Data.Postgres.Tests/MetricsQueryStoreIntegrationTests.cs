using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Metrics;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

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
            MetricWindow.H1,
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
            MetricWindow.H1,
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
            MetricWindow.H1,
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
            MetricWindow.H1,
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
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddUserAliases(alias =>
            {
                alias.DisplayValue = "personal@example.com";
                alias.NormalizedValue = "personal@example.com";
            })
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member@company.com";

        await SeedMetricsAsync(
            AgentMetric(
                org,
                "zeeq_agent_token_usage",
                100,
                "gpt-5-codex",
                "personal@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            MetricWindow.H1,
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
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddUserAliases(alias =>
            {
                alias.DisplayValue = "disabled-personal@example.com";
                alias.NormalizedValue = "disabled-personal@example.com";
                alias.DisabledAtUtc = now;
            })
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
            MetricWindow.H1,
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
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddUserAliases(alias =>
            {
                alias.DisplayValue = "personal-no-email@example.com";
                alias.NormalizedValue = "personal-no-email@example.com";
            })
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
            MetricWindow.H1,
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
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddUserAliases(alias =>
            {
                alias.DisplayValue = "personal-filter@example.com";
                alias.NormalizedValue = "personal-filter@example.com";
            })
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
            MetricWindow.H1,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(Users: ["member-filter@company.com"]),
            CancellationToken.None
        );
        var rawAliasSeries = await store.GetSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            MetricWindow.H1,
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
        var (seed, _) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddUserAliases(alias =>
            {
                alias.DisplayValue = "personal-2d@example.com";
                alias.NormalizedValue = "personal-2d@example.com";
            })
            .BuildAsync();
        var org = seed.Organization.Id;
        seed.Owner.Email = "member-2d@company.com";

        await SeedMetricsAsync(
            AgentMetric(
                org,
                "zeeq_agent_token_usage",
                75,
                "gpt-5-codex",
                "personal-2d@example.com"
            )
        );

        var store = new PostgresMetricsQueryStore(_context);
        var canonicalSeries = await store.GetTwoDimensionalSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            MetricWindow.H1,
            MetricSeriesGroup.Model,
            MetricSeriesGroup.User,
            new MetricSeriesFilters(Users: ["member-2d@company.com"]),
            CancellationToken.None
        );
        var rawAliasSeries = await store.GetTwoDimensionalSeriesAsync(
            org,
            "zeeq_agent_token_usage",
            MetricWindow.H1,
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
            MetricWindow.H1,
            MetricSeriesGroup.None,
            new MetricSeriesFilters(Users: ["a@x.com", "b@x.com"]),
            CancellationToken.None
        );

        await Assert.That(series.Sum(p => p.Value)).IsEqualTo(2d);
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
            MetricWindow.H1,
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
            MetricWindow.H1,
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
            MetricWindow.H1,
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
            MetricWindow.H1,
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
            MetricWindow.H1,
            repositoryIds: null,
            authorLogins: null,
            ReviewFindingsGroup.Repo,
            CancellationToken.None
        );

        await Assert.That(series.Sum(p => p.Critical)).IsEqualTo(5L);
        await Assert.That(series.Sum(p => p.Major)).IsEqualTo(5L);
    }

    [Test]
    public async Task GetReviewVolume_GroupByRepo_ResolvesDisplayNameEvenWhenSoftDeleted()
    {
        // The repository row still exists (just soft-deleted), so review rows whose repo mapping was
        // later removed still resolve to a readable name rather than the raw id. code_review_repositories
        // has an org FK, so seed a real organization graph first.
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var org = seed.Organization.Id;

        await SeedRepositoriesAsync(Repository(org, "repo_q", "acme/widgets", disabled: true));
        await SeedReviewsAsync(Review(org), Review(org));

        var store = new PostgresMetricsQueryStore(_context);
        var series = await store.GetReviewVolumeSeriesAsync(
            org,
            MetricWindow.H1,
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
            MetricWindow.H1,
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
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var org = seed.Organization.Id;

        await SeedMetricsAsync(
            ToolCall(org, "search_documents", user: "alice@x.com"),
            ToolCall(org, "read_document", user: "bob@x.com"),
            ToolCall(org, "search_documents", user: "alice@x.com")
        );
        await SeedRepositoriesAsync(Repository(org, "repo_deleted", "acme/old", disabled: true));
        await SeedReviewsAsync(Review(org));

        var store = new PostgresMetricsQueryStore(_context);
        var options = await store.GetFilterOptionsAsync(org, CancellationToken.None);

        await Assert.That(options.Users.Count).IsEqualTo(2);
        await Assert.That(options.Users.Contains("alice@x.com")).IsTrue();
        await Assert.That(options.Tools.Contains("search_documents")).IsTrue();
        await Assert.That(options.Authors.Contains("octocat")).IsTrue();
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

    private static MetricEvent Read(
        string org,
        string metricType,
        string path,
        string? heading = null
    ) =>
        new()
        {
            OrganizationId = org,
            MetricType = metricType,
            MetricValue = 1,
            Library = "zeeq-app",
            Tags = heading is null
                ? new() { ["path"] = path }
                : new() { ["path"] = path, ["heading"] = heading },
            CreatedAtUtc = Now,
        };

    private static CodeReviewRecord Review(
        string org,
        CodeReviewRequestOrigin origin = CodeReviewRequestOrigin.RepositoryWebhook,
        int critical = 0,
        int major = 0
    ) =>
        new()
        {
            Id = $"cr_{Guid.CreateVersion7():N}",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            OrganizationId = org,
            RepositoryId = "repo_q",
            OwnerQualifiedRepoName = "acme/repo",
            Branch = "main",
            Title = "Test review",
            AuthorLogin = "octocat",
            Status = CodeReviewStatus.Completed,
            RequestOrigin = origin,
            CriticalFindings = critical,
            MajorFindings = major,
        };
}
