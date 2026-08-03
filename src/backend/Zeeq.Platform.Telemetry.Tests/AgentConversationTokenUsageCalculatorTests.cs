using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Read;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Tests for the reduced conversation token-usage summarizer. Feeds pre-aggregated
/// <see cref="AgentCompletionModelAggregate"/> rows directly — the same shape
/// <c>PostgresAgentConversationQueryStore</c>'s SQL-side <c>GROUP BY</c> produces — rather
/// than raw completion events, since the calculator no longer sees per-event rows.
/// </summary>
[Category("Unit")]
public sealed class AgentConversationTokenUsageCalculatorTests
{
    [Test]
    public async Task Summarize_NoAggregates_ReturnsNull()
    {
        var summary = AgentConversationTokenUsageCalculator.Summarize([]);

        await Assert.That(summary).IsNull();
    }

    [Test]
    public async Task Summarize_SumsTokensAndTracksPeaks()
    {
        // Same model group as a real SQL GROUP BY would produce from two completion events
        // (input 1000/2000, cached 400/500, output 200/300, reasoning 50/100, cost 0.01/0.02).
        var aggregates = new[]
        {
            new AgentCompletionModelAggregate(
                "claude-sonnet-5",
                EventCount: 2,
                SumInputTokens: 3000,
                SumCachedTokens: 900,
                SumOutputTokens: 500,
                SumReasoningTokens: 150,
                SumToolTokens: 0,
                SumCostUsd: 0.03m,
                EventsMissingCost: 0,
                MaxInputTokens: 2000,
                MaxCachedTokens: 500
            ),
        };

        var summary = AgentConversationTokenUsageCalculator.Summarize(aggregates);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.CompletionEventCount).IsEqualTo(2);
        await Assert.That(summary.PeakInputTokens).IsEqualTo(2000);
        await Assert.That(summary.PeakCachedInputTokens).IsEqualTo(500);
        await Assert.That(summary.BilledInputTokens).IsEqualTo(3000);
        await Assert.That(summary.BilledCachedInputTokens).IsEqualTo(900);
        await Assert.That(summary.BilledFreshInputTokens).IsEqualTo(2100);
        await Assert.That(summary.BilledOutputTokens).IsEqualTo(500);
        await Assert.That(summary.BilledReasoningTokens).IsEqualTo(150);
        await Assert.That(summary.CacheHitRate).IsEqualTo(0.3m);
        await Assert.That(summary.TotalCostUsd).IsEqualTo(0.03m);
        await Assert.That(summary.AverageCostPerEventUsd).IsEqualTo(0.015m);
    }

    [Test]
    public async Task Summarize_ClampsCachedAndReasoningToTheirTotals()
    {
        // Malformed telemetry: cached/reasoning subsets larger than their totals.
        var aggregates = new[]
        {
            new AgentCompletionModelAggregate(
                "claude-sonnet-5",
                EventCount: 1,
                SumInputTokens: 100,
                SumCachedTokens: 500,
                SumOutputTokens: 10,
                SumReasoningTokens: 999,
                SumToolTokens: 0,
                SumCostUsd: 0m,
                EventsMissingCost: 1,
                MaxInputTokens: 100,
                MaxCachedTokens: 500
            ),
        };

        var summary = AgentConversationTokenUsageCalculator.Summarize(aggregates);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.BilledCachedInputTokens).IsEqualTo(100);
        await Assert.That(summary.BilledFreshInputTokens).IsEqualTo(0);
        await Assert.That(summary.BilledReasoningTokens).IsEqualTo(10);
        await Assert.That(summary.TotalCostUsd).IsNull();
        await Assert.That(summary.AverageCostPerEventUsd).IsNull();
    }

    [Test]
    public async Task Summarize_OneEventMissingCost_TotalCostIsNullNotPartialSum()
    {
        // One priced event and one un-costed event in the same group: the total must not
        // silently understate the conversation's true cost by pretending the un-costed
        // event contributed $0.
        var aggregates = new[]
        {
            new AgentCompletionModelAggregate(
                "claude-sonnet-5",
                EventCount: 2,
                SumInputTokens: 300,
                SumCachedTokens: 0,
                SumOutputTokens: 150,
                SumReasoningTokens: 0,
                SumToolTokens: 0,
                SumCostUsd: 0.01m,
                EventsMissingCost: 1,
                MaxInputTokens: 200,
                MaxCachedTokens: 0
            ),
        };

        var summary = AgentConversationTokenUsageCalculator.Summarize(aggregates);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.TotalCostUsd).IsNull();
        await Assert.That(summary.AverageCostPerEventUsd).IsNull();
    }

    [Test]
    public async Task Summarize_UnknownModel_StillProducesCostBreakdownFromDefaultRates()
    {
        var aggregates = new[]
        {
            new AgentCompletionModelAggregate(
                "some-future-model-nobody-has-heard-of",
                EventCount: 1,
                SumInputTokens: 1_000_000,
                SumCachedTokens: 0,
                SumOutputTokens: 1_000_000,
                SumReasoningTokens: 0,
                SumToolTokens: 0,
                SumCostUsd: 5m,
                EventsMissingCost: 0,
                MaxInputTokens: 1_000_000,
                MaxCachedTokens: 0
            ),
        };

        var summary = AgentConversationTokenUsageCalculator.Summarize(aggregates);

        await Assert.That(summary).IsNotNull();
        // Default catalog rate: $2.50/$15.00 per million input/output tokens. Deliberately NOT
        // reconciled with TotalCostUsd below — see the calculator's remarks: the breakdown is
        // an independent catalog-rate estimate, not a component split of the persisted total.
        await Assert.That(summary!.FreshInputCostUsd).IsEqualTo(2.50m);
        await Assert.That(summary.OutputCostUsd).IsEqualTo(15.00m);
        await Assert.That(summary.TotalCostUsd).IsEqualTo(5m);
    }

    [Test]
    public async Task Summarize_MultipleModelGroups_PricesEachAtItsOwnRateAndTracksOverallPeaks()
    {
        // A conversation that switched models mid-stream: each group must be priced at its
        // own model's rate, and peaks/sums must combine across groups, not just use the last one.
        var aggregates = new[]
        {
            new AgentCompletionModelAggregate(
                "claude-sonnet-5",
                EventCount: 1,
                SumInputTokens: 1000,
                SumCachedTokens: 0,
                SumOutputTokens: 100,
                SumReasoningTokens: 0,
                SumToolTokens: 0,
                SumCostUsd: 0.01m,
                EventsMissingCost: 0,
                MaxInputTokens: 1000,
                MaxCachedTokens: 0
            ),
            new AgentCompletionModelAggregate(
                "claude-opus-4-8",
                EventCount: 1,
                SumInputTokens: 5000,
                SumCachedTokens: 0,
                SumOutputTokens: 50,
                SumReasoningTokens: 0,
                SumToolTokens: 0,
                SumCostUsd: 0.05m,
                EventsMissingCost: 0,
                MaxInputTokens: 5000,
                MaxCachedTokens: 0
            ),
        };

        var summary = AgentConversationTokenUsageCalculator.Summarize(aggregates);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.CompletionEventCount).IsEqualTo(2);
        await Assert.That(summary.PeakInputTokens).IsEqualTo(5000);
        await Assert.That(summary.BilledInputTokens).IsEqualTo(6000);
        await Assert.That(summary.BilledOutputTokens).IsEqualTo(150);
        await Assert.That(summary.TotalCostUsd).IsEqualTo(0.06m);
    }
}
