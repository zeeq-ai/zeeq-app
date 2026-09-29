using Zeeq.Core.Llm;
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
/// Pricing rates are sourced from provider API pricing pages as of September 29, 2026.
/// The embedded catalog is version-stamped; rate changes should bump the version.
/// Previously stored cost estimates are not recalculated by this enricher.
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
            // JSON-import adapter identity (Pi and any other first-party import
            // client) — the caller-reported harness lives on the conversation,
            // not here, so estimate from tokens whenever cost wasn't self-reported.
            "zeeq-agent" => EnrichFromTokens(evt),
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
