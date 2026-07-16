using Zeeq.Platform.Telemetry.Adapters;

namespace Zeeq.Platform.Telemetry.Processing;

/// <summary>
/// Post-adapter cost enrichment: converts raw billing metrics (tokens, nano-AIU)
/// into <see cref="AgentSessionEventRecord.CostUsd"/> with a traceable
/// <see cref="AgentSessionEventRecord.CostSource"/>.
/// </summary>
/// <remarks>
/// Adapters extract raw metrics only — the enricher is the single place where
/// pricing rules live, so rate changes can be applied without touching adapter
/// logic. Non-completion events and events that already have a cost source
/// (e.g. Claude's reported USD) pass through unchanged.
/// </remarks>
public interface IAgentTelemetryCostEnricher
{
    /// <summary>
    /// Enriches a completion event with estimated or converted cost in USD.
    /// Returns a new record with <see cref="AgentSessionEventRecord.CostUsd"/>
    /// and <see cref="AgentSessionEventRecord.CostSource"/> populated when
    /// applicable. Non-completion events and already-costed events return as-is.
    /// </summary>
    /// <param name="evt">The adapter-produced event record.</param>
    /// <param name="harnessName">The harness that produced the event.</param>
    AgentSessionEventRecord Enrich(AgentSessionEventRecord evt, string harnessName);
}
