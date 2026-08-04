using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Testing;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the Sessions inbox/detail read store backed by Postgres.
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[Category("Integration")]
[NotInParallel("agent-conversation-query-store")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresAgentConversationQueryStoreIntegrationTests(PgDatabaseFixture postgres)
{
    [Test]
    public async Task ListRecentAsync_OrdersNewestFirstAndPaginatesByStartedAtUtc()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConversationQueryStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var orgId = $"org-{Guid.CreateVersion7():N}";
        var now = DateTimeOffset.UtcNow;
        const string subjectUserId = "user-a";

        var oldest = Conversation(orgId, "oldest", now.AddMinutes(-20));
        oldest.CreatedById = subjectUserId;
        var middle = Conversation(orgId, "middle", now.AddMinutes(-10));
        middle.CreatedById = subjectUserId;
        var newest = Conversation(orgId, "newest", now);
        newest.CreatedById = subjectUserId;

        db.AgentConversations.AddRange(oldest, middle, newest);
        await db.SaveChangesAsync();

        var firstPage = await store.ListRecentAsync(
            new AgentConversationStreamQuery(orgId, subjectUserId, PageSize: 1),
            CancellationToken.None
        );

        await Assert.That(firstPage.Items.Select(item => item.Id)).IsEquivalentTo(["newest"]);
        await Assert.That(firstPage.NextCursor).IsNotNull();

        var secondPage = await store.ListRecentAsync(
            new AgentConversationStreamQuery(
                orgId,
                subjectUserId,
                Cursor: firstPage.NextCursor,
                PageSize: 2
            ),
            CancellationToken.None
        );

        await Assert
            .That(secondPage.Items.Select(item => item.Id))
            .IsEquivalentTo(["middle", "oldest"]);
        // The second page exhausts every remaining row, so it must not carry a cursor —
        // otherwise the client would issue one guaranteed-empty "load more" request.
        await Assert.That(secondPage.NextCursor).IsNull();
    }

    /// <summary>
    /// The inbox has no "All" option — this is the only ownership boundary the store
    /// enforces, so it's the only place that boundary needs a test.
    /// </summary>
    [Test]
    public async Task ListRecentAsync_OnlyReturnsCallersOwnConversations()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConversationQueryStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var orgId = $"org-{Guid.CreateVersion7():N}";
        var now = DateTimeOffset.UtcNow;

        var mine = Conversation(orgId, "mine", now);
        mine.CreatedById = "user-a";
        var theirs = Conversation(orgId, "theirs", now.AddMinutes(-1));
        theirs.CreatedById = "user-b";

        db.AgentConversations.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var mineScoped = await store.ListRecentAsync(
            new AgentConversationStreamQuery(orgId, "user-a"),
            CancellationToken.None
        );

        await Assert.That(mineScoped.Items.Select(item => item.Id)).IsEquivalentTo(["mine"]);
    }

    [Test]
    public async Task ListRecentAsync_ProjectsReadyAndRecomputingRollupState()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConversationQueryStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var orgId = $"org-{Guid.CreateVersion7():N}";
        const string subjectUserId = "user-a";
        var now = DateTimeOffset.UtcNow;

        var ready = Conversation(orgId, "ready", now);
        ready.CreatedById = subjectUserId;
        ready.Title = "first real prompt";
        ready.TotalInputTokens = 100;
        ready.TotalOutputTokens = 20;
        ready.TotalCostUsd = 0.12m;
        ready.RollupVersion = AgentConversationRollupVersion.Current;

        var recomputing = Conversation(orgId, "recomputing", now.AddSeconds(-1));
        recomputing.CreatedById = subjectUserId;
        recomputing.Title = "already captured title";
        recomputing.TotalInputTokens = 999;
        recomputing.TotalOutputTokens = 999;
        recomputing.TotalCostUsd = 999;
        recomputing.RollupVersion = AgentConversationRollupVersion.Current + 1;

        db.AgentConversations.AddRange(ready, recomputing);
        await db.SaveChangesAsync();

        var page = await store.ListRecentAsync(
            new AgentConversationStreamQuery(orgId, subjectUserId),
            CancellationToken.None
        );

        var readySummary = page.Items.Single(item => item.Id == "ready");
        await Assert.That(readySummary.Title).IsEqualTo("first real prompt");
        await Assert.That(readySummary.RollupStatus).IsEqualTo(AgentConversationRollupStatus.Ready);
        await Assert.That(readySummary.TotalInputTokens).IsEqualTo(100L);
        await Assert.That(readySummary.TotalOutputTokens).IsEqualTo(20L);
        await Assert.That(readySummary.TotalCostUsd).IsEqualTo(0.12m);

        var recomputingSummary = page.Items.Single(item => item.Id == "recomputing");
        await Assert.That(recomputingSummary.Title).IsEqualTo("already captured title");
        await Assert
            .That(recomputingSummary.RollupStatus)
            .IsEqualTo(AgentConversationRollupStatus.Recomputing);
        await Assert.That(recomputingSummary.TotalInputTokens).IsNull();
        await Assert.That(recomputingSummary.TotalOutputTokens).IsNull();
        await Assert.That(recomputingSummary.TotalCostUsd).IsNull();
    }

    [Test]
    public async Task GetDetailAsync_ReturnsPromptsAscendingAndCompletionUsageRowsOnly()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConversationQueryStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var orgId = $"org-{Guid.CreateVersion7():N}";
        var conversationId = $"conversation-{Guid.CreateVersion7():N}";
        var startedAtUtc = DateTimeOffset.UtcNow;

        db.AgentConversations.Add(Conversation(orgId, conversationId, startedAtUtc));
        db.AgentSessionEvents.AddRange(
            PromptEvent(orgId, conversationId, startedAtUtc.AddSeconds(2), "second prompt"),
            PromptEvent(orgId, conversationId, startedAtUtc.AddSeconds(1), "first prompt"),
            ToolResultEvent(orgId, conversationId, startedAtUtc.AddSeconds(3)),
            CompletionEvent(orgId, conversationId, startedAtUtc.AddSeconds(4), inputTokens: 100, outputTokens: 20),
            CompletionEvent(orgId, conversationId, startedAtUtc.AddSeconds(5), inputTokens: 50, outputTokens: 10)
        );
        await db.SaveChangesAsync();

        var detail = await store.GetDetailAsync(orgId, conversationId, CancellationToken.None);

        await Assert.That(detail).IsNotNull();
        await Assert
            .That(detail!.Prompts.Select(p => p.PromptText!))
            .IsEquivalentTo(["first prompt", "second prompt"]);
        await Assert.That(detail.UsageAggregates.Sum(a => a.EventCount)).IsEqualTo(2);
        await Assert.That(detail.UsageAggregates.Sum(a => a.SumInputTokens)).IsEqualTo(150);
    }

    /// <summary>
    /// Regression test for a real gap found in production data: Codex and Pi conversations
    /// never set <c>prompt_group_id</c> on completion events (100% null), so per-turn token
    /// attachment must not depend on it — a time-window sweep between consecutive prompts
    /// is used instead, and this asserts it correctly splits completions by turn even
    /// though every event here has a null PromptGroupId.
    /// </summary>
    [Test]
    public async Task GetDetailAsync_AttachesPerTurnTokensByTimeWindow_EvenWithoutPromptGroupId()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConversationQueryStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var orgId = $"org-{Guid.CreateVersion7():N}";
        var conversationId = $"conversation-{Guid.CreateVersion7():N}";
        var startedAtUtc = DateTimeOffset.UtcNow;

        db.AgentConversations.Add(Conversation(orgId, conversationId, startedAtUtc));
        db.AgentSessionEvents.AddRange(
            // First turn: prompt at +1s, its completion at +2s (before the next prompt).
            PromptEvent(orgId, conversationId, startedAtUtc.AddSeconds(1), "first prompt"),
            CompletionEvent(orgId, conversationId, startedAtUtc.AddSeconds(2), inputTokens: 100, outputTokens: 20),
            // Second turn: prompt at +3s, two completions after it (last one open-ended).
            PromptEvent(orgId, conversationId, startedAtUtc.AddSeconds(3), "second prompt"),
            CompletionEvent(orgId, conversationId, startedAtUtc.AddSeconds(4), inputTokens: 50, outputTokens: 10),
            CompletionEvent(orgId, conversationId, startedAtUtc.AddSeconds(5), inputTokens: 25, outputTokens: 5),
            // Third turn: prompt at +6s, no completions yet (still in flight).
            PromptEvent(orgId, conversationId, startedAtUtc.AddSeconds(6), "third prompt")
        );
        await db.SaveChangesAsync();

        var detail = await store.GetDetailAsync(orgId, conversationId, CancellationToken.None);

        await Assert.That(detail).IsNotNull();
        var prompts = detail!.Prompts;
        await Assert.That(prompts.All(p => p.PromptGroupId == null)).IsTrue();

        var first = prompts.Single(p => p.PromptText == "first prompt");
        var second = prompts.Single(p => p.PromptText == "second prompt");
        var third = prompts.Single(p => p.PromptText == "third prompt");

        await Assert.That(first.TurnInputTokens).IsEqualTo(100L);
        await Assert.That(first.TurnOutputTokens).IsEqualTo(20L);
        await Assert.That(second.TurnInputTokens).IsEqualTo(75L);
        await Assert.That(second.TurnOutputTokens).IsEqualTo(15L);
        await Assert.That(third.TurnInputTokens).IsNull();
        await Assert.That(third.TurnOutputTokens).IsNull();
    }

    /// <summary>
    /// Regression test for the newest-500 prompt cap: when a conversation exceeds it, the
    /// oldest prompts are trimmed (not the newest), and any completion event that belonged to
    /// a trimmed prompt's turn must be skipped rather than folded into the first *kept*
    /// prompt's turn tokens. Also confirms the completions query itself is never capped, so
    /// downstream cost totals always see every completion regardless of the prompt window.
    /// </summary>
    [Test]
    public async Task GetDetailAsync_PromptCapKeepsNewestAndIgnoresCompletionsFromTrimmedPrompts()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConversationQueryStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var orgId = $"org-{Guid.CreateVersion7():N}";
        var conversationId = $"conversation-{Guid.CreateVersion7():N}";
        var startedAtUtc = DateTimeOffset.UtcNow;

        const int promptCount = 505;
        var prompts = Enumerable
            .Range(0, promptCount)
            .Select(i =>
                PromptEvent(orgId, conversationId, startedAtUtc.AddSeconds(i + 1), $"prompt {i}")
            )
            .ToArray();

        db.AgentConversations.Add(Conversation(orgId, conversationId, startedAtUtc));
        db.AgentSessionEvents.AddRange(prompts);

        // Between prompt 3 (+4s) and prompt 4 (+5s) — both destined to be trimmed, since only
        // the newest 500 of 505 are kept (indices 0-4 drop). Once they're gone, this must not
        // be folded into the first *kept* prompt's ("prompt 5", +6s) turn.
        db.AgentSessionEvents.Add(
            CompletionEvent(
                orgId,
                conversationId,
                startedAtUtc.AddSeconds(4.5),
                inputTokens: 999_000,
                outputTokens: 999_000
            )
        );

        // Well after the last prompt — confirms the final open-ended window still works once
        // the cap makes the last kept prompt genuinely the conversation's last prompt.
        db.AgentSessionEvents.Add(
            CompletionEvent(
                orgId,
                conversationId,
                startedAtUtc.AddSeconds(promptCount + 10),
                inputTokens: 42,
                outputTokens: 7
            )
        );

        await db.SaveChangesAsync();

        var detail = await store.GetDetailAsync(orgId, conversationId, CancellationToken.None);

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.Prompts.Count).IsEqualTo(500);
        await Assert.That(detail.Prompts.First().PromptText).IsEqualTo("prompt 5");
        await Assert.That(detail.Prompts.Last().PromptText).IsEqualTo($"prompt {promptCount - 1}");

        // The trimmed-window completion's 999,000 tokens must not appear on any kept prompt.
        await Assert
            .That(detail.Prompts.Select(p => p.TurnInputTokens ?? 0).Max())
            .IsLessThan(999_000);

        var last = detail.Prompts.Last();
        await Assert.That(last.TurnInputTokens).IsEqualTo(42L);
        await Assert.That(last.TurnOutputTokens).IsEqualTo(7L);

        // Usage aggregates are never capped: both completions count toward the total — even
        // the trimmed-window one, which correctly does NOT show up in any prompt's turn tokens
        // above, but must still count toward the conversation's authoritative cost/token total.
        await Assert.That(detail.UsageAggregates.Sum(a => a.EventCount)).IsEqualTo(2);
        await Assert.That(detail.UsageAggregates.Sum(a => a.SumInputTokens)).IsEqualTo(999_042);
    }

    /// <summary>
    /// Regression test for the SQL-side <c>GROUP BY</c> usage aggregate — the part of this
    /// query with real EF-to-Postgres translation risk, since it combines <c>GroupBy</c> with
    /// <c>Sum</c>/<c>Max</c>/<c>Count</c> over nullable columns. Verifies grouping by model,
    /// summing across events within a group, tracking the group's peak, and counting events
    /// with no persisted cost, all against a real database rather than trusting the LINQ
    /// compiles.
    /// </summary>
    [Test]
    public async Task GetDetailAsync_UsageAggregatesGroupByModelWithCorrectSumsMaxesAndCounts()
    {
        await using var provider = CreateProvider(postgres.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentConversationQueryStore>();
        var db = scope.ServiceProvider.GetRequiredService<PostgresDbContext>();
        var orgId = $"org-{Guid.CreateVersion7():N}";
        var conversationId = $"conversation-{Guid.CreateVersion7():N}";
        var startedAtUtc = DateTimeOffset.UtcNow;

        db.AgentConversations.Add(Conversation(orgId, conversationId, startedAtUtc));
        db.AgentSessionEvents.AddRange(
            // Two "claude-sonnet-5" events, one missing its cost.
            CompletionEvent(
                orgId,
                conversationId,
                startedAtUtc.AddSeconds(1),
                inputTokens: 1000,
                outputTokens: 100,
                model: "claude-sonnet-5",
                costUsd: 0.01m,
                cachedTokens: 200
            ),
            CompletionEvent(
                orgId,
                conversationId,
                startedAtUtc.AddSeconds(2),
                inputTokens: 3000,
                outputTokens: 300,
                model: "claude-sonnet-5",
                costUsd: null,
                cachedTokens: 500
            ),
            // One "claude-opus-4-8" event, fully priced.
            CompletionEvent(
                orgId,
                conversationId,
                startedAtUtc.AddSeconds(3),
                inputTokens: 200,
                outputTokens: 20,
                model: "claude-opus-4-8",
                costUsd: 0.5m,
                cachedTokens: 0
            )
        );
        await db.SaveChangesAsync();

        var detail = await store.GetDetailAsync(orgId, conversationId, CancellationToken.None);

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.UsageAggregates.Count).IsEqualTo(2);

        var sonnet = detail.UsageAggregates.Single(a => a.Model == "claude-sonnet-5");
        await Assert.That(sonnet.EventCount).IsEqualTo(2);
        await Assert.That(sonnet.SumInputTokens).IsEqualTo(4000);
        await Assert.That(sonnet.SumCachedTokens).IsEqualTo(700);
        await Assert.That(sonnet.SumOutputTokens).IsEqualTo(400);
        await Assert.That(sonnet.MaxInputTokens).IsEqualTo(3000);
        await Assert.That(sonnet.MaxCachedTokens).IsEqualTo(500);
        await Assert.That(sonnet.SumCostUsd).IsEqualTo(0.01m);
        await Assert.That(sonnet.EventsMissingCost).IsEqualTo(1);

        var opus = detail.UsageAggregates.Single(a => a.Model == "claude-opus-4-8");
        await Assert.That(opus.EventCount).IsEqualTo(1);
        await Assert.That(opus.SumInputTokens).IsEqualTo(200);
        await Assert.That(opus.SumCostUsd).IsEqualTo(0.5m);
        await Assert.That(opus.EventsMissingCost).IsEqualTo(0);
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddPostgres(
            new AppSettings
            {
                Database = new DatabaseSettings { ConnectionString = connectionString },
            }
        );

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static AgentConversation Conversation(
        string organizationId,
        string id,
        DateTimeOffset startedAtUtc
    ) =>
        new()
        {
            OrganizationId = organizationId,
            Id = id,
            Harness = "claude-code",
            StartedAtUtc = startedAtUtc,
            RollupVersion = AgentConversationRollupVersion.Current,
        };

    private static AgentSessionEvent PromptEvent(
        string organizationId,
        string conversationId,
        DateTimeOffset occurredAtUtc,
        string promptText
    ) =>
        new()
        {
            Id = Guid.CreateVersion7().ToString("N"),
            OrganizationId = organizationId,
            ConversationId = conversationId,
            OccurredAtUtc = occurredAtUtc,
            EventType = AgentSessionEventType.Prompt,
            PromptText = promptText,
            PromptLength = promptText.Length,
        };

    private static AgentSessionEvent ToolResultEvent(
        string organizationId,
        string conversationId,
        DateTimeOffset occurredAtUtc
    ) =>
        new()
        {
            Id = Guid.CreateVersion7().ToString("N"),
            OrganizationId = organizationId,
            ConversationId = conversationId,
            OccurredAtUtc = occurredAtUtc,
            EventType = AgentSessionEventType.ToolResult,
            ToolName = "test_tool",
            Success = true,
        };

    private static AgentSessionEvent CompletionEvent(
        string organizationId,
        string conversationId,
        DateTimeOffset occurredAtUtc,
        int inputTokens,
        int outputTokens,
        string model = "claude-sonnet-5",
        decimal? costUsd = null,
        int? cachedTokens = null
    ) =>
        new()
        {
            Id = Guid.CreateVersion7().ToString("N"),
            OrganizationId = organizationId,
            ConversationId = conversationId,
            OccurredAtUtc = occurredAtUtc,
            EventType = AgentSessionEventType.Completion,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CostUsd = costUsd,
            CachedTokens = cachedTokens,
        };
}
