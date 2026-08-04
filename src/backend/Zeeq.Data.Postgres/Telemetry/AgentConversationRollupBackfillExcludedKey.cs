using System.Text.Json.Serialization;

namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Conversation key excluded from one backfill claim attempt.
/// </summary>
/// <remarks>
/// The backfill worker can temporarily skip a conversation that timed out and continue
/// claiming other stale rows. This record is serialized to JSONB and expanded by
/// <c>jsonb_to_recordset</c> inside the claim query, so the JSON property names are the
/// database-facing snake_case contract.
/// </remarks>
/// <param name="OrganizationId">Organization that owns the conversation.</param>
/// <param name="ConversationId">Conversation id to skip for this claim attempt.</param>
internal sealed record AgentConversationRollupBackfillExcludedKey(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("conversation_id")] string ConversationId
);
