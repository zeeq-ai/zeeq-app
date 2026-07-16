using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Zeeq.Core.Models;

/// <summary>
/// Unified append-only event log for agent sessions. Partitioned by <c>occurred_at_utc</c>
/// (14-day interval). Replaces v1's separate per-type event tables.
/// </summary>
/// <remarks>
/// Event schema is harness-agnostic; all three harnesses (Claude Code, Codex, Copilot Chat)
/// map to these columns. Sparse columns are cheap in Postgres (null columns are stored
/// compactly). One table is simpler than three (single partition scheme, single BRIN index,
/// simpler conversation reconstruction).
///
/// Correlation keys: <c>prompt_group_id</c> unifies a turn's requests/tools;
/// <c>tool_call_id</c> correlates tool result to decision/request;
/// <c>provider_request_id</c> is per-completion.
/// </remarks>
[Table("agent_session_events")]
public sealed class AgentSessionEvent
{
    /// <summary>UUID v7 identifier.</summary>
    [Key]
    public required string Id { get; set; }

    /// <summary>Owning organization; distribution key and partition scope.</summary>
    public required string OrganizationId { get; set; }

    /// <summary>FK to <c>agent_conversations</c> (composite with <c>OrganizationId</c>).</summary>
    public required string ConversationId { get; set; }

    /// <summary>Partition key (RANGE 14-day).</summary>
    public required DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Claude <c>event.sequence</c>; null for harnesses without a sequence.</summary>
    public long? SourceSequence { get; set; }

    /// <summary>Codex <c>logId</c> or other stable source-record tie-breaker.</summary>
    public string? SourceRecordId { get; set; }

    /// <summary>Event type discriminator: prompt, tool_result, tool_decision, completion, turn_summary.</summary>
    public AgentSessionEventType EventType { get; set; }

    // --- Correlation ---

    /// <summary>Claude <c>prompt.id</c> / Copilot <c>trace_id</c> (unifies one turn).</summary>
    public string? PromptGroupId { get; set; }

    /// <summary>Claude <c>tool_use_id</c> / Codex <c>call_id</c> / Copilot <c>gen_ai.tool.call.id</c>.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>Claude <c>request_id</c> / Copilot <c>gen_ai.response.id</c>.</summary>
    public string? ProviderRequestId { get; set; }

    // --- Prompt fields ---

    /// <summary>User prompt text (subject to obfuscation policy).</summary>
    public string? PromptText { get; set; }

    /// <summary>Prompt text length.</summary>
    public int? PromptLength { get; set; }

    // --- Tool fields ---

    /// <summary>Normalized <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> where possible.</summary>
    public string? ToolName { get; set; }

    /// <summary>Verbatim harness value (Codex mangled, Copilot truncated).</summary>
    public string? ToolNameRaw { get; set; }

    /// <summary>MCP server name.</summary>
    public string? McpServer { get; set; }

    /// <summary>MCP server origin (Claude only).</summary>
    public string? McpServerOrigin { get; set; }

    /// <summary>MCP server scope (Claude only).</summary>
    public string? McpServerScope { get; set; }

    /// <summary>MCP tool input arguments (JSONB).</summary>
    [Column(TypeName = "jsonb")]
    public JsonDocument? ArgumentsJson { get; set; }

    /// <summary>Capped tool output (16 KiB).</summary>
    public string? OutputSnippet { get; set; }

    /// <summary>Whether the tool call succeeded.</summary>
    public bool? Success { get; set; }

    /// <summary>Tool call duration in milliseconds.</summary>
    public int? DurationMs { get; set; }

    /// <summary><c>accept</c> | <c>reject</c> (tool_decision).</summary>
    public string? Decision { get; set; }

    /// <summary><c>config</c> | <c>user_temporary</c> | ... (tool_decision source).</summary>
    public string? DecisionSource { get; set; }

    // --- Completion fields ---

    /// <summary>Model name reported by the harness.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Total input token count, including <see cref="CachedTokens"/> when present.
    /// </summary>
    /// <remarks>
    /// Providers that report fresh and cached tokens separately are normalized before persistence so
    /// cost estimation can charge regular input as <c>InputTokens - CachedTokens</c>.
    /// </remarks>
    public int? InputTokens { get; set; }

    /// <summary>
    /// Cached token count included in <see cref="InputTokens"/> (collapsed from cache_read + cache_creation).
    /// </summary>
    public int? CachedTokens { get; set; }

    /// <summary>Output token count.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Reasoning token count (Copilot only).</summary>
    public int? ReasoningTokens { get; set; }

    /// <summary>Tool token count (Codex only).</summary>
    public int? ToolTokens { get; set; }

    /// <summary>Estimated or reported cost in USD.</summary>
    [Column(TypeName = "numeric(14,6)")]
    public decimal? CostUsd { get; set; }

    /// <summary>Origin of <see cref="CostUsd"/>.</summary>
    public AgentSessionEventCostSource? CostSource { get; set; }

    /// <summary>Copilot nano-AIU (undocumented field; nullable-tolerant).</summary>
    public long? CostUnitsRaw { get; set; }

    /// <summary>Claude <c>query_source</c> / Copilot <c>gen_ai.agent.name</c>.</summary>
    public string? QuerySource { get; set; }

    /// <summary>Whether this event is housekeeping (title-gen, progressMessages, etc.).</summary>
    public bool IsHousekeeping { get; set; }
}

/// <summary>Discriminates the type of agent session event row.</summary>
public enum AgentSessionEventType : byte
{
    /// <summary>A user prompt event.</summary>
    Prompt = 1,

    /// <summary>A tool execution result event.</summary>
    ToolResult = 2,

    /// <summary>A tool decision event (accept/reject).</summary>
    ToolDecision = 3,

    /// <summary>A completion event with token usage and cost.</summary>
    Completion = 4,

    /// <summary>Future: aggregated per-turn summary row.</summary>
    TurnSummary = 5,
}

/// <summary>Origin of the cost estimate on a completion row.</summary>
public enum AgentSessionEventCostSource : byte
{
    /// <summary>Cost was reported directly by the provider.</summary>
    ReportedUsd = 1,

    /// <summary>Cost was estimated from token counts and a pricing catalog.</summary>
    EstimatedFromTokens = 2,

    /// <summary>Cost is expressed in billing units (e.g. Copilot nano-AIU).</summary>
    BillingUnits = 3,
}
