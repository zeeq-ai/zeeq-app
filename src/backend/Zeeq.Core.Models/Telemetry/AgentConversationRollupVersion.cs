namespace Zeeq.Core.Models;

/// <summary>
/// Current conversation rollup algorithm version recognized by the running code.
/// </summary>
/// <remarks>
/// Release B treats version-one rows as current. Older rows remain readable but their stored
/// rollup totals are projected as recomputing until the backfill worker advances them.
/// </remarks>
public static class AgentConversationRollupVersion
{
    /// <summary>Version of the rollup algorithm treated as current by this binary.</summary>
    public const int Current = 1;
}
