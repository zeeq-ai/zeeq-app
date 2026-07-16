using System.Text.Json;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Adapters;

/// <summary>
/// Converts one agent telemetry record (log or span) into a conversation observation
/// and optional unified session event row.
/// </summary>
/// <remarks>
/// Adapters stay side-effect free. The background processor owns raw row
/// leasing, storage ordering, logging, and deletion.
/// </remarks>
public interface IAgentTelemetryAdapter
{
    /// <summary>
    /// Harness name for deterministic ordering and diagnostics.
    /// </summary>
    string HarnessName { get; }

    /// <summary>
    /// Determines whether this adapter can interpret the log record context.
    /// </summary>
    bool CanHandle(TelemetryLogRecordContext record);

    /// <summary>
    /// Converts the log record context into domain artifacts.
    /// </summary>
    AgentTelemetryAdapterResult Adapt(TelemetryLogRecordContext record);

    /// <summary>
    /// Determines whether this adapter can interpret the span record context.
    /// </summary>
    bool CanHandle(TelemetrySpanRecordContext record);

    /// <summary>
    /// Converts the span record context into domain artifacts.
    /// </summary>
    AgentTelemetryAdapterResult Adapt(TelemetrySpanRecordContext record);
}

/// <summary>
/// Result of adapting one record (log or span) into a conversation observation and
/// optional event row. v2 uses a single event row with an <c>event_type</c>
/// discriminator, reflecting the unified <c>agent_session_events</c> table.
/// </summary>
public sealed record AgentTelemetryAdapterResult
{
    /// <summary>Partial conversation observation to merge.</summary>
    public required AgentConversationObservation Conversation { get; init; }

    /// <summary>At most one event per record.</summary>
    public AgentSessionEventRecord? Event { get; init; }

    /// <summary>Creates a result with an event row.</summary>
    public static AgentTelemetryAdapterResult WithEvent(
        AgentConversationObservation obs,
        AgentSessionEventRecord evt
    ) => new() { Conversation = obs, Event = evt };

    /// <summary>Creates a conversation-only result (no event row).</summary>
    public static AgentTelemetryAdapterResult ConversationOnly(AgentConversationObservation obs) =>
        new() { Conversation = obs };
}

/// <summary>
/// Partial conversation observation produced by an adapter. The processing service
/// merges multiple observations from the same conversation into one durable row.
/// </summary>
public sealed class AgentConversationObservation
{
    /// <summary>Agent conversation identifier.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Organization identifier.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Harness name (e.g. <c>claude-code</c>, <c>codex</c>).</summary>
    public required string Harness { get; init; }

    /// <summary>Owner email address.</summary>
    public string? OwnerEmail { get; set; }

    /// <summary>Harness variant (e.g. desktop vs. plugin).</summary>
    public string? HarnessVariant { get; set; }

    /// <summary>Application version.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Repository remote URL.</summary>
    public string? RepoRemoteUrl { get; set; }

    /// <summary>Repository head branch.</summary>
    public string? HeadBranch { get; set; }

    /// <summary>Repository head SHA.</summary>
    public string? HeadSha { get; set; }

    /// <summary>Model name.</summary>
    public string? Model { get; set; }
}

/// <summary>
/// One event row extracted from a telemetry record, using the unified event schema.
/// This is an adapter-side DTO — it is not an EF Core entity. The
/// <c>ArgumentsJson</c> field uses <see cref="JsonDocument"/> because the adapter
/// produces structured MCP tool inputs directly. The EF entity
/// <see cref="AgentSessionEvent"/> maps this to Npgsql's native <c>jsonb</c> type
/// via <c>.HasColumnType("jsonb")</c> without a custom value converter.
/// </summary>
/// <param name="EventType">Event type discriminator.</param>
/// <param name="PromptText">User prompt text.</param>
/// <param name="PromptLength">Prompt text length.</param>
/// <param name="ToolName">Normalized <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</param>
/// <param name="ToolNameRaw">Verbatim harness value.</param>
/// <param name="McpServer">MCP server name.</param>
/// <param name="McpServerOrigin">MCP server origin.</param>
/// <param name="McpServerScope">MCP server scope.</param>
/// <param name="ArgumentsJson">MCP tool input arguments.</param>
/// <param name="OutputSnippet">Capped tool output.</param>
/// <param name="Success">Whether the tool call succeeded.</param>
/// <param name="DurationMs">Tool call duration in milliseconds.</param>
/// <param name="Decision"><c>accept</c> | <c>reject</c>.</param>
/// <param name="DecisionSource">Decision source.</param>
/// <param name="Model">Model name.</param>
/// <param name="InputTokens">
/// Total input token count, including <paramref name="CachedTokens"/> when present.
/// Adapters whose providers report fresh and cached tokens separately must normalize them to this total.
/// </param>
/// <param name="CachedTokens">Cached token count included in <paramref name="InputTokens"/>.</param>
/// <param name="OutputTokens">Output token count.</param>
/// <param name="ReasoningTokens">Reasoning token count.</param>
/// <param name="ToolTokens">Tool token count.</param>
/// <param name="CostUsd">Reported or estimated cost.</param>
/// <param name="CostSource">Origin of cost.</param>
/// <param name="CostUnitsRaw">Billing units (Copilot nano-AIU).</param>
/// <param name="QuerySource">Agent or query source name.</param>
/// <param name="IsHousekeeping">Whether this is a housekeeping event.</param>
/// <param name="OccurredAtUtc">When the event occurred.</param>
/// <param name="SourceSequence">Claude event sequence.</param>
/// <param name="SourceRecordId">Codex logId tie-breaker.</param>
/// <param name="PromptGroupId">Claude prompt.id / Copilot trace_id.</param>
/// <param name="ToolCallId">Tool call correlation ID.</param>
/// <param name="ProviderRequestId">Per-completion request ID.</param>
public sealed record AgentSessionEventRecord(
    AgentSessionEventType EventType,
    string? PromptText = null,
    int? PromptLength = null,
    string? ToolName = null,
    string? ToolNameRaw = null,
    string? McpServer = null,
    string? McpServerOrigin = null,
    string? McpServerScope = null,
    JsonDocument? ArgumentsJson = null,
    string? OutputSnippet = null,
    bool? Success = null,
    int? DurationMs = null,
    string? Decision = null,
    string? DecisionSource = null,
    string? Model = null,
    int? InputTokens = null,
    int? CachedTokens = null,
    int? OutputTokens = null,
    int? ReasoningTokens = null,
    int? ToolTokens = null,
    decimal? CostUsd = null,
    AgentSessionEventCostSource? CostSource = null,
    long? CostUnitsRaw = null,
    string? QuerySource = null,
    bool IsHousekeeping = false,
    DateTimeOffset? OccurredAtUtc = null,
    long? SourceSequence = null,
    string? SourceRecordId = null,
    string? PromptGroupId = null,
    string? ToolCallId = null,
    string? ProviderRequestId = null
);
