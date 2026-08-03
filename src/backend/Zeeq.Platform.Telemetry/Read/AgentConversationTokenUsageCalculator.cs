using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Processing;

namespace Zeeq.Platform.Telemetry.Read;

/// <summary>
/// Conversation-level token usage and cost summary derived from completion events.
/// </summary>
/// <remarks>
/// Reduced port of <c>motion/biblio</c>'s <c>AgentConversationTokenUsageSummary</c>,
/// adapted to Zeeq's flat per-token <see cref="PricingCatalog"/> (no long-context
/// tiering, unlike v1's threshold-based model profiles — dropped as a deliberate
/// simplification, not an oversight). <see cref="TotalCostUsd"/> sums the cost already
/// persisted on each event by <see cref="AgentTelemetryCostEnricher"/> — it is not
/// recomputed here.
///
/// NOTE: the fresh/cached/output/cache-savings breakdown is <em>not</em> a reconciled
/// split of <see cref="TotalCostUsd"/> — each is independently priced from
/// <see cref="PricingCatalog"/> rates applied to this conversation's token sums, so they
/// can (and in practice do, whenever a completion event's persisted cost was itself an
/// estimate from a different rate source) sum to a different figure than
/// <see cref="TotalCostUsd"/>. Treat the breakdown as "what this would cost at today's
/// catalog rates," not as an itemization of the authoritative total.
/// </remarks>
/// <param name="CompletionEventCount">Number of completion events summarized.</param>
/// <param name="PeakInputTokens">Largest single-event input token count observed.</param>
/// <param name="PeakCachedInputTokens">Largest single-event cached-input token count observed.</param>
/// <param name="BilledFreshInputTokens">Sum of non-cached input tokens across all events.</param>
/// <param name="BilledCachedInputTokens">Sum of cached input tokens across all events.</param>
/// <param name="BilledInputTokens">Sum of total input tokens (fresh + cached) across all events.</param>
/// <param name="BilledOutputTokens">Sum of output tokens across all events.</param>
/// <param name="BilledReasoningTokens">Sum of reasoning tokens (subset of output; Copilot only).</param>
/// <param name="BilledToolTokens">Sum of tool tokens (Codex only).</param>
/// <param name="CacheHitRate">Cached input tokens as a share of total input tokens.</param>
/// <param name="ReasoningShareOfOutput">Reasoning tokens as a share of total output tokens.</param>
/// <param name="TotalCostUsd">Authoritative total — sum of each event's already-persisted cost.</param>
/// <param name="AverageCostPerEventUsd"><paramref name="TotalCostUsd"/> divided by <paramref name="CompletionEventCount"/>.</param>
/// <param name="FreshInputCostUsd">
/// Independent catalog-rate estimate for fresh input tokens — not a component of
/// <paramref name="TotalCostUsd"/>; see the remarks above.
/// </param>
/// <param name="CachedInputCostUsd">Independent catalog-rate estimate for cached input tokens; see <paramref name="FreshInputCostUsd"/>.</param>
/// <param name="OutputCostUsd">Independent catalog-rate estimate for output tokens; see <paramref name="FreshInputCostUsd"/>.</param>
/// <param name="CacheSavingsUsd">Estimated USD saved versus pricing all input tokens at the fresh rate.</param>
public sealed record AgentConversationTokenUsageSummary(
    int CompletionEventCount,
    int PeakInputTokens,
    int PeakCachedInputTokens,
    long BilledFreshInputTokens,
    long BilledCachedInputTokens,
    long BilledInputTokens,
    long BilledOutputTokens,
    long BilledReasoningTokens,
    long BilledToolTokens,
    decimal? CacheHitRate,
    decimal? ReasoningShareOfOutput,
    decimal? TotalCostUsd,
    decimal? AverageCostPerEventUsd,
    decimal? FreshInputCostUsd,
    decimal? CachedInputCostUsd,
    decimal? OutputCostUsd,
    decimal? CacheSavingsUsd
);

/// <summary>
/// Pure token usage calculator for one conversation's completion events.
/// </summary>
public static class AgentConversationTokenUsageCalculator
{
    /// <summary>
    /// Builds one conversation token usage summary from per-model completion aggregates.
    /// </summary>
    /// <param name="aggregates">
    /// Per-model sums from <see cref="IAgentConversationQueryStore.GetDetailAsync"/>'s
    /// SQL-side <c>GROUP BY</c> — never raw per-event rows, so this stays cheap even for a
    /// conversation with tens of thousands of completion events.
    /// </param>
    /// <returns>A usage summary, or <see langword="null"/> when there are no completion events.</returns>
    public static AgentConversationTokenUsageSummary? Summarize(
        IReadOnlyList<AgentCompletionModelAggregate> aggregates
    )
    {
        if (aggregates.Count == 0)
        {
            return null;
        }

        var completionEventCount = 0;
        var peakInputTokens = 0;
        var peakCachedInputTokens = 0;
        long billedFreshInputTokens = 0;
        long billedCachedInputTokens = 0;
        long billedInputTokens = 0;
        long billedOutputTokens = 0;
        long billedReasoningTokens = 0;
        long billedToolTokens = 0;
        decimal freshInputCostUsd = 0;
        decimal cachedInputCostUsd = 0;
        decimal outputCostUsd = 0;
        decimal cacheSavingsUsd = 0;
        decimal totalCostUsd = 0;
        var hasCompleteCost = true;

        foreach (var aggregate in aggregates)
        {
            completionEventCount += aggregate.EventCount;

            // Clamped at the aggregate level (not per event, since these are already SQL-side
            // sums): malformed telemetry (a cached/reasoning subset larger than its total)
            // must not produce a negative fresh-input or non-reasoning count. This is
            // equivalent to a strict per-event clamp except in the pathological mixed case
            // (some events in this model group malformed, others not) — an acceptable trade
            // for never materializing every completion event into memory to clamp them
            // individually.
            var input = Math.Max(aggregate.SumInputTokens, 0);
            var cached = Math.Clamp(aggregate.SumCachedTokens, 0, input);
            var output = Math.Max(aggregate.SumOutputTokens, 0);
            var reasoning = Math.Clamp(aggregate.SumReasoningTokens, 0, output);
            var tool = Math.Max(aggregate.SumToolTokens, 0);
            var freshInput = input - cached;

            peakInputTokens = Math.Max(peakInputTokens, aggregate.MaxInputTokens);
            peakCachedInputTokens = Math.Max(peakCachedInputTokens, aggregate.MaxCachedTokens);
            billedFreshInputTokens += freshInput;
            billedCachedInputTokens += cached;
            billedInputTokens += input;
            billedOutputTokens += output;
            billedReasoningTokens += reasoning;
            billedToolTokens += tool;

            // 👈 Authoritative: SumCostUsd is whatever AgentTelemetryCostEnricher already
            // persisted (reported or estimated) at ingest time, summed as-is, not redone.
            totalCostUsd += aggregate.SumCostUsd;
            if (aggregate.EventsMissingCost > 0)
            {
                // ❌ At least one un-costed event in this group means the sum is a partial
                // total, not the conversation's true cost — must not be presented as
                // authoritative.
                hasCompleteCost = false;
            }

            // Looked up per model group, not once for the conversation: a long-running session
            // can switch models mid-stream (e.g. a manual model-tier change), and each group's
            // own model is what actually priced those events.
            var rates = PricingCatalog.Lookup(aggregate.Model);
            freshInputCostUsd += freshInput * rates.InputPerToken;
            cachedInputCostUsd += cached * rates.CachedPerToken;
            outputCostUsd += output * rates.OutputPerToken;
            cacheSavingsUsd += cached * (rates.InputPerToken - rates.CachedPerToken);
        }

        // null (not a partial sum) unless every completion event carried a persisted cost —
        // distinguishes "$0 spent" and "fully known" from "some/all costs unknown" so the UI
        // shows "—" instead of a misleadingly low total.
        decimal? resolvedTotalCostUsd = hasCompleteCost ? totalCostUsd : null;
        var averageCostPerEventUsd = resolvedTotalCostUsd is { } total && completionEventCount > 0
            ? total / completionEventCount
            : (decimal?)null;

        return new AgentConversationTokenUsageSummary(
            completionEventCount,
            peakInputTokens,
            peakCachedInputTokens,
            billedFreshInputTokens,
            billedCachedInputTokens,
            billedInputTokens,
            billedOutputTokens,
            billedReasoningTokens,
            billedToolTokens,
            billedInputTokens == 0 ? null : (decimal)billedCachedInputTokens / billedInputTokens,
            billedOutputTokens == 0 ? null : (decimal)billedReasoningTokens / billedOutputTokens,
            resolvedTotalCostUsd,
            averageCostPerEventUsd,
            freshInputCostUsd,
            cachedInputCostUsd,
            outputCostUsd,
            cacheSavingsUsd
        );
    }
}
