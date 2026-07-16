using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.Platform.Telemetry.Ingest.Import;

/// <summary>
/// Request body for importing agent telemetry events as JSON.
/// </summary>
/// <remarks>
/// The authenticated user and organization come from the request principal, not
/// from payload fields. Repository fields are observational metadata used later
/// to associate conversations with pull requests.
/// </remarks>
/// <param name="ConversationId">Stable conversation identifier assigned by the source harness.</param>
/// <param name="HarnessName">Source harness name, such as <c>codex</c> or <c>claude-code</c>.</param>
/// <param name="Events">Telemetry events to import for the conversation.</param>
/// <param name="HarnessVersion">Optional source harness version.</param>
/// <param name="RepositoryRemoteUrl">Optional Git remote URL for the working repository.</param>
/// <param name="HeadBranch">Optional Git branch active when the events were captured.</param>
/// <param name="HeadSha">Optional Git commit SHA active when the events were captured.</param>
public sealed record AgentTelemetryImportRequest(
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("harness_name")] string HarnessName,
    [property: JsonPropertyName("events"), MinLength(1)] IReadOnlyList<ImportedAgentEvent> Events,
    [property: JsonPropertyName("harness_version")] string? HarnessVersion = null,
    [property: JsonPropertyName("repository_remote_url")] string? RepositoryRemoteUrl = null,
    [property: JsonPropertyName("head_branch")] string? HeadBranch = null,
    [property: JsonPropertyName("head_sha")] string? HeadSha = null
);

/// <summary>
/// One event in a direct telemetry import request.
/// </summary>
/// <param name="Kind">Type of event represented by this record.</param>
/// <param name="EventId">Optional stable event identifier from the source harness.</param>
/// <param name="OccurredAtUtc">Optional timestamp for when the event occurred.</param>
/// <param name="PromptText">Prompt text for prompt events, when capture is enabled.</param>
/// <param name="PromptLength">Prompt length in characters.</param>
/// <param name="ToolName">Tool name for tool-result events.</param>
/// <param name="ToolCallId">Tool call correlation identifier.</param>
/// <param name="McpServerName">MCP server name for MCP-backed tool calls.</param>
/// <param name="ToolArguments">Structured tool-call arguments.</param>
/// <param name="ToolResult">Tool-call result or output snippet.</param>
/// <param name="ToolDurationMs">Tool-call duration in milliseconds.</param>
/// <param name="ToolSucceeded">Whether the tool call completed successfully.</param>
/// <param name="Model">Model name for prompt or completion events.</param>
/// <param name="InputTokens">Total input token count for completion events.</param>
/// <param name="CachedTokens">Cached-token subset of <paramref name="InputTokens"/>.</param>
/// <param name="OutputTokens">Output token count for completion events.</param>
/// <param name="ReasoningTokens">Reasoning token count for completion events.</param>
/// <param name="CostUsd">Reported completion cost in USD.</param>
/// <param name="UserEmail">Observed source user email. This does not authorize the request.</param>
/// <param name="OrganizationId">Observed source organization id. This does not authorize the request.</param>
public sealed record ImportedAgentEvent(
    [property: JsonPropertyName("kind")] AgentEventKind Kind,
    [property: JsonPropertyName("event.id")] string? EventId = null,
    [property: JsonPropertyName("event.timestamp")] DateTimeOffset? OccurredAtUtc = null,
    [property: JsonPropertyName("prompt_text")] string? PromptText = null,
    [property: JsonPropertyName("prompt_length")] int? PromptLength = null,
    [property: JsonPropertyName("gen_ai.tool.name")] string? ToolName = null,
    [property: JsonPropertyName("gen_ai.tool.call.id")] string? ToolCallId = null,
    [property: JsonPropertyName("mcp.server.name")] string? McpServerName = null,
    [property: JsonPropertyName("gen_ai.tool.call.arguments")] JsonElement? ToolArguments = null,
    [property: JsonPropertyName("gen_ai.tool.call.result")] string? ToolResult = null,
    [property: JsonPropertyName("gen_ai.tool.call.duration_ms")] int? ToolDurationMs = null,
    [property: JsonPropertyName("gen_ai.tool.call.success")] bool? ToolSucceeded = null,
    [property: JsonPropertyName("gen_ai.request.model")] string? Model = null,
    [property: JsonPropertyName("gen_ai.usage.input_tokens")] int? InputTokens = null,
    [property: JsonPropertyName("gen_ai.usage.cached_tokens")] int? CachedTokens = null,
    [property: JsonPropertyName("gen_ai.usage.output_tokens")] int? OutputTokens = null,
    [property: JsonPropertyName("gen_ai.usage.reasoning_tokens")] int? ReasoningTokens = null,
    [property: JsonPropertyName("zeeq.cost.usd")] decimal? CostUsd = null,
    [property: JsonPropertyName("user.email")] string? UserEmail = null,
    [property: JsonPropertyName("organization_id")] string? OrganizationId = null
);

/// <summary>
/// Event kinds accepted by the JSON telemetry import endpoint.
/// </summary>
public enum AgentEventKind : byte
{
    /// <summary>A user prompt or prompt-like agent input.</summary>
    Prompt = 1,

    /// <summary>A completed tool call.</summary>
    ToolResult = 2,

    /// <summary>A model completion with optional token and cost data.</summary>
    Completion = 3,
}

/// <summary>
/// Response returned after imported events are accepted for telemetry processing.
/// </summary>
/// <param name="EventsAccepted">Number of events accepted by the shared ingest path.</param>
public sealed record AgentTelemetryImportResponse(int EventsAccepted);
