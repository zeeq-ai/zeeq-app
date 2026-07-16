using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.Data.Postgres.Telemetry;

internal sealed record AgentSessionEventInsertRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("occurred_at_utc")] DateTimeOffset OccurredAtUtc,
    [property: JsonPropertyName("source_sequence")] long? SourceSequence,
    [property: JsonPropertyName("source_record_id")] string? SourceRecordId,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("event_type")] byte EventType,
    [property: JsonPropertyName("prompt_group_id")] string? PromptGroupId,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId,
    [property: JsonPropertyName("provider_request_id")] string? ProviderRequestId,
    [property: JsonPropertyName("prompt_text")] string? PromptText,
    [property: JsonPropertyName("prompt_length")] int? PromptLength,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("tool_name_raw")] string? ToolNameRaw,
    [property: JsonPropertyName("mcp_server")] string? McpServer,
    [property: JsonPropertyName("mcp_server_origin")] string? McpServerOrigin,
    [property: JsonPropertyName("mcp_server_scope")] string? McpServerScope,
    [property: JsonPropertyName("arguments_json")] JsonElement? ArgumentsJson,
    [property: JsonPropertyName("output_snippet")] string? OutputSnippet,
    [property: JsonPropertyName("success")] bool? Success,
    [property: JsonPropertyName("duration_ms")] int? DurationMs,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("decision_source")] string? DecisionSource,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("cached_tokens")] int? CachedTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("reasoning_tokens")] int? ReasoningTokens,
    [property: JsonPropertyName("tool_tokens")] int? ToolTokens,
    [property: JsonPropertyName("cost_usd")] decimal? CostUsd,
    [property: JsonPropertyName("cost_source")] byte? CostSource,
    [property: JsonPropertyName("cost_units_raw")] long? CostUnitsRaw,
    [property: JsonPropertyName("query_source")] string? QuerySource,
    [property: JsonPropertyName("is_housekeeping")] bool IsHousekeeping
);
