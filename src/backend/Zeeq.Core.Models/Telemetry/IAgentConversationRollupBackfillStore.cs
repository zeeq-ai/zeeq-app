namespace Zeeq.Core.Models;

/// <summary>
/// Storage contract for finite, versioned conversation rollup repair.
/// </summary>
public interface IAgentConversationRollupBackfillStore
{
    /// <summary>
    /// Claims and recomputes one stale conversation, promoting the row only after absolute
    /// title/token/cost values are written.
    /// </summary>
    Task<AgentConversationRollupBackfillResult> BackfillNextAsync(
        int targetVersion,
        TimeSpan statementTimeout,
        IReadOnlySet<AgentConversationKey> excludedKeys,
        CancellationToken cancellationToken
    );
}

/// <summary>Outcome of one backfill claim attempt.</summary>
public sealed record AgentConversationRollupBackfillResult(
    AgentConversationRollupBackfillStatus Status,
    AgentConversationKey? ConversationKey = null
);

/// <summary>Stable statuses for a single backfill attempt.</summary>
public enum AgentConversationRollupBackfillStatus
{
    /// <summary>One stale conversation was recomputed and promoted.</summary>
    Completed,

    /// <summary>No eligible stale conversation was available to claim.</summary>
    NoWork,

    /// <summary>The claimed conversation exceeded the configured statement timeout.</summary>
    TimedOut,
}
