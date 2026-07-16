namespace Zeeq.Core.Models;

/// <summary>
/// Ownership classification for an agent telemetry conversation.
/// </summary>
/// <remarks>
/// API authorization must use <c>CreatedById</c>, not raw telemetry identity
/// hints. This status explains whether the telemetry identity was trusted enough
/// to populate that authorization key.
/// Values are persisted as strings in the database for operator readability; do not
/// rename members without adding a data migration.
/// </remarks>
public enum AgentConversationOwnershipStatus
{
    /// <summary>
    /// Telemetry identity was preserved for reconciliation but not trusted for UI ownership.
    /// </summary>
    Unmatched = 0,

    /// <summary>
    /// Telemetry email matched the authenticated Zeeq user identity.
    /// </summary>
    MatchedToUserEmail = 1,

    /// <summary>
    /// Authenticated ingest principal was stamped at raw write time (v2).
    /// </summary>
    MatchedToIngestPrincipal = 2,
}
