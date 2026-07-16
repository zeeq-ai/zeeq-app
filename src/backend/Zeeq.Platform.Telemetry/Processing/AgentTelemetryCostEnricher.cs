using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;

namespace Zeeq.Platform.Telemetry.Processing;

/// <summary>
/// Post-adapter cost enrichment for completion events. Converts raw billing
/// metrics (token counts, nano-AIU) into <see cref="AgentSessionEventRecord.CostUsd"/>
/// with a traceable <see cref="AgentSessionEventRecord.CostSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// Adapters extract raw metrics only — this enricher is the single place where
/// pricing rules live, so rate changes can be applied without touching adapter
/// logic. Non-completion events and events that already have a cost source
/// (e.g. Claude's reported USD) pass through unchanged.
/// </para>
/// <para>
/// Pricing rates are sourced from provider API pricing pages as of July 2026.
/// The embedded catalog is version-stamped; future rate changes should bump the
/// version and add new entries (keeping old entries for historical accuracy).
/// Unknown models are estimated using <c>default</c> catch-all rates.
/// </para>
/// </remarks>
public sealed class AgentTelemetryCostEnricher : IAgentTelemetryCostEnricher
{
    /// <inheritdoc />
    public AgentSessionEventRecord Enrich(AgentSessionEventRecord evt, string harnessName)
    {
        if (evt.EventType != AgentSessionEventType.Completion)
        {
            return evt;
        }

        if (evt.CostSource.HasValue)
        {
            return evt;
        }

        return harnessName switch
        {
            "copilot-chat" => EnrichCopilot(evt),
            "codex" => EnrichFromTokens(evt),
            _ => evt,
        };
    }

    /// <summary>
    /// Enriches a Copilot completion. Prefers token-based estimation when model
    /// and token counts are available; falls back to nano-AIU conversion.
    /// </summary>
    private static AgentSessionEventRecord EnrichCopilot(AgentSessionEventRecord evt)
    {
        if (evt.InputTokens.HasValue || evt.OutputTokens.HasValue)
        {
            return EnrichFromTokens(evt);
        }

        if (evt.CostUnitsRaw.HasValue)
        {
            var costUsd = evt.CostUnitsRaw.Value * PricingCatalog.CopilotNanoAiuToUsdRate;
            return evt with
            {
                CostUsd = Math.Round(costUsd, 6),
                CostSource = AgentSessionEventCostSource.BillingUnits,
            };
        }

        return evt;
    }

    /// <summary>
    /// Estimates cost from token counts using per-model rates from the pricing catalog.
    /// Unknown models fall back to a weighted average of input/output rates. Returns
    /// the event unchanged when no token metrics are present (prevents a false $0
    /// estimate on tokenless completions).
    /// </summary>
    private static AgentSessionEventRecord EnrichFromTokens(AgentSessionEventRecord evt)
    {
        if (evt.InputTokens is null && evt.CachedTokens is null && evt.OutputTokens is null)
        {
            return evt;
        }

        var rates = PricingCatalog.Lookup(evt.Model);

        var input = evt.InputTokens ?? 0;
        var cached = evt.CachedTokens ?? 0;
        var output = evt.OutputTokens ?? 0;

        // Adapter contract: InputTokens is the total input count and CachedTokens is its cached subset.
        // OpenAI reports prompt_tokens as the total (cached_tokens is a subset).
        //   See: https://platform.openai.com/docs/guides/prompt-caching
        // Anthropic reports input_tokens as only the fresh portion, with
        // cache_read_input_tokens and cache_creation_input_tokens as separate fields.
        //   See: https://docs.anthropic.com/en/docs/build-with-claude/prompt-caching#pricing
        // Adapters that receive fresh and cached values separately normalize them to this contract.
        var regularInput = Math.Max(0, input - cached);

        var costUsd =
            regularInput * rates.InputPerToken
            + cached * rates.CachedPerToken
            + output * rates.OutputPerToken;

        return evt with
        {
            CostUsd = Math.Round(costUsd, 6),
            CostSource = AgentSessionEventCostSource.EstimatedFromTokens,
        };
    }
}

/// <summary>
/// Per-model token pricing rates sourced from provider API pricing pages as of
/// July 2026. Rates are per-token (not per-million). Cached token rate applies
/// to cache-read and cache-write tokens combined.
/// </summary>
/// <remarks>
/// Version-stamped so historical estimates remain traceable. When rates change,
/// append new entries and bump the version — do not mutate existing entries.
/// </remarks>
internal static class PricingCatalog
{
    /// <summary>
    /// Catalog version for traceability. Bump when rates are updated.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Copilot nano-AIU → USD conversion rate.
    /// 1 AI credit = $0.01. The nano-AIU → AI credit ratio is internal to
    /// GitHub. This rate is calibrated against observed Copilot trace data and
    /// should be updated when the mapping is published or changes.
    /// </summary>
    /// <remarks>
    /// Derived from observed Copilot Chat spans: a typical completion with
    /// ~10K input + ~1K output tokens costs roughly $0.03-0.05 in comparable
    /// API pricing. Observed nano-AIU values for similar completions are in the
    /// range of 3-5 million, implying ~$1e-8 per nano-AIU.
    /// </remarks>
    public const decimal CopilotNanoAiuToUsdRate = 1.0e-8m;

    private static readonly Dictionary<string, TokenRates> _rates = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        // --- OpenAI models (pricing as of July 2026) ---
        // Source: https://platform.openai.com/docs/pricing (standard tier)
        ["gpt-5.6-sol"] = TokenRates.FromPerMillion(5.00m, 0.50m, 30.00m),
        ["gpt-5.6-terra"] = TokenRates.FromPerMillion(2.50m, 0.25m, 15.00m),
        ["gpt-5.6-luna"] = TokenRates.FromPerMillion(1.00m, 0.10m, 6.00m),
        ["gpt-5.5"] = TokenRates.FromPerMillion(5.00m, 0.50m, 30.00m),
        ["gpt-5.4"] = TokenRates.FromPerMillion(2.50m, 0.25m, 15.00m),
        ["gpt-5.4-mini"] = TokenRates.FromPerMillion(0.75m, 0.075m, 4.50m),
        ["gpt-5.4-nano"] = TokenRates.FromPerMillion(0.20m, 0.02m, 1.25m),
        ["gpt-5.3-codex"] = TokenRates.FromPerMillion(1.75m, 0.175m, 14.00m),

        // --- Anthropic models (pricing as of July 2026) ---
        // Source: https://www.anthropic.com/pricing (API, standard tier)
        ["claude-sonnet-5"] = TokenRates.FromPerMillion(2.00m, 0.20m, 10.00m),
        ["claude-haiku-4.5"] = TokenRates.FromPerMillion(1.00m, 0.10m, 5.00m),
        ["claude-opus-4.8"] = TokenRates.FromPerMillion(5.00m, 0.50m, 25.00m),
        ["claude-fable-5"] = TokenRates.FromPerMillion(10.00m, 1.00m, 50.00m),
        ["claude-sonnet-4.6"] = TokenRates.FromPerMillion(3.00m, 0.30m, 15.00m),
        ["claude-sonnet-4.5"] = TokenRates.FromPerMillion(3.00m, 0.30m, 15.00m),
        ["claude-opus-4.7"] = TokenRates.FromPerMillion(5.00m, 0.50m, 25.00m),
        ["claude-opus-4.6"] = TokenRates.FromPerMillion(5.00m, 0.50m, 25.00m),
        ["claude-opus-4.5"] = TokenRates.FromPerMillion(5.00m, 0.50m, 25.00m),
        ["claude-opus-4.1"] = TokenRates.FromPerMillion(15.00m, 1.50m, 75.00m),

        // --- Default fallback (weighted-average of mid-tier models) ---
        ["default"] = TokenRates.FromPerMillion(2.50m, 0.25m, 15.00m),
    };

    /// <summary>
    /// Looks up token rates for a model name. Returns the <c>default</c>
    /// entry for unknown models.
    /// </summary>
    /// <param name="modelName">The model name from the telemetry record.</param>
    public static TokenRates Lookup(string? modelName)
    {
        if (modelName is null)
        {
            return _rates["default"];
        }

        return _rates.TryGetValue(modelName, out var rates) ? rates : _rates["default"];
    }
}

/// <summary>
/// Per-token pricing rates for a model. All values are USD per token
/// (divide by 1,000,000 for the common per-1M-token display convention).
/// </summary>
internal readonly record struct TokenRates(
    decimal InputPerToken,
    decimal CachedPerToken,
    decimal OutputPerToken
)
{
    /// <summary>
    /// Creates rates from the per-1M-token pricing convention displayed on
    /// provider pricing pages.
    /// </summary>
    public static TokenRates FromPerMillion(
        decimal inputPer1M,
        decimal cachedPer1M,
        decimal outputPer1M
    ) => new(inputPer1M / 1_000_000m, cachedPer1M / 1_000_000m, outputPer1M / 1_000_000m);
}
