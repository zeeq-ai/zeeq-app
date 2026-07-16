namespace Zeeq.Core.Models;

/// <summary>
/// Zeeq model quality tier requested by a code reviewer agent.
/// </summary>
/// <remarks>
/// The tier is app-semantic configuration, not a provider model name. Runtime
/// execution resolves it through organization LLM settings or the system-level
/// default for the same tier.
/// </remarks>
public enum CodeReviewModelTier
{
    /// <summary>Lower-latency review tier.</summary>
    Fast,

    /// <summary>Balanced high-quality review tier.</summary>
    High,

    /// <summary>Highest-quality review tier.</summary>
    Max,
}
