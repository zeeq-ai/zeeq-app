namespace Zeeq.Core.Models;

/// <summary>
/// Current conversation rollup algorithm version recognized by the running code.
/// </summary>
/// <remarks>
/// Release A deliberately keeps this at zero while inline writes are deployed. Release B bumps
/// it to one, enables the backfill worker, and makes API reads treat version-one rows as ready.
/// </remarks>
public static class AgentConversationRollupVersion
{
    /// <summary>Version of the rollup algorithm treated as current by this binary.</summary>
    public const int Current = 0;
}
