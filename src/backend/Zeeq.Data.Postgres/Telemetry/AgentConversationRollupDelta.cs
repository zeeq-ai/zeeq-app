using System.Text.Json.Serialization;

namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Incremental rollup contribution for one conversation in one ingest transaction.
/// </summary>
/// <remarks>
/// Built only from <c>agent_session_events</c> rows returned by
/// <c>INSERT ... ON CONFLICT DO NOTHING RETURNING</c>. That keeps replayed telemetry from
/// double-counting. The record is serialized to JSONB and consumed by a set-wise
/// <c>UPDATE agent_conversations FROM jsonb_to_recordset(...)</c>, so the JSON property names
/// are the database-facing snake_case contract used by that SQL.
/// </remarks>
/// <param name="OrganizationId">Organization that owns the conversation.</param>
/// <param name="ConversationId">Conversation receiving this delta.</param>
/// <param name="Title">First non-empty, non-housekeeping prompt candidate in this inserted batch.</param>
/// <param name="InputTokensDelta">Input tokens to add across inserted completion events.</param>
/// <param name="OutputTokensDelta">Output tokens to add across inserted completion events.</param>
/// <param name="KnownCostUsdDelta">Known persisted cost to add when no completion cost is missing.</param>
/// <param name="CompletionCountDelta">Inserted completion event count in this batch.</param>
/// <param name="MissingCostCompletionCountDelta">Inserted completion count with no persisted cost.</param>
internal sealed record AgentConversationRollupDelta(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("input_tokens_delta")] long InputTokensDelta,
    [property: JsonPropertyName("output_tokens_delta")] long OutputTokensDelta,
    [property: JsonPropertyName("known_cost_usd_delta")] decimal KnownCostUsdDelta,
    [property: JsonPropertyName("completion_count_delta")] long CompletionCountDelta,
    [property: JsonPropertyName("missing_cost_completion_count_delta")]
        long MissingCostCompletionCountDelta
);
