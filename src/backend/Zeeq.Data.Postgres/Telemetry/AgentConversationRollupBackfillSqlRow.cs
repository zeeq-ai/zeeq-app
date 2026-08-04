namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Claimed conversation identity returned by the rollup backfill claim SQL.
/// </summary>
/// <remarks>
/// This is a private SQL projection type, not a domain model. The backfill store uses it to
/// hold the row locked by <c>FOR UPDATE SKIP LOCKED</c>, then passes the same key into the
/// absolute recompute statement and returns it in the backfill result for observability or
/// temporary timeout exclusion.
/// </remarks>
internal sealed class AgentConversationRollupBackfillSqlRow
{
    /// <summary>Organization that owns the claimed conversation.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Conversation id claimed by the backfill transaction.</summary>
    public required string ConversationId { get; init; }
}
