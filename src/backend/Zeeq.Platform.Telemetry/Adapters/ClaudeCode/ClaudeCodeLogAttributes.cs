namespace Zeeq.Platform.Telemetry.Adapters.ClaudeCode;

/// <summary>
/// Claude Code OTLP log names and attributes used by telemetry ingestion.
/// </summary>
public static class ClaudeCodeLogAttributes
{
    /// <summary>Harness identifier for Claude Code.</summary>
    public const string HarnessName = "claude-code";

    /// <summary>OTLP instrumentation scope name for Claude Code events.</summary>
    public const string SourceName = "com.anthropic.claude_code.events";

    /// <summary>User prompt event name.</summary>
    public const string UserPromptEventName = "claude_code.user_prompt";

    /// <summary>API request event name.</summary>
    public const string ApiRequestEventName = "claude_code.api_request";

    /// <summary>Tool result event name.</summary>
    public const string ToolResultEventName = "claude_code.tool_result";

    /// <summary>Tool decision event name.</summary>
    public const string ToolDecisionEventName = "claude_code.tool_decision";

    /// <summary>MCP server connection event name.</summary>
    public const string McpServerConnectionEventName = "claude_code.mcp_server_connection";

    /// <summary>Claude Code session identifier.</summary>
    public const string SessionId = "session.id";

    /// <summary>Event timestamp attribute.</summary>
    public const string EventTimestamp = "event.timestamp";

    /// <summary>Event sequence number.</summary>
    public const string EventSequence = "event.sequence";

    /// <summary>User identifier.</summary>
    public const string UserId = "user.id";

    /// <summary>User email address.</summary>
    public const string UserEmail = "user.email";

    /// <summary>User account identifier.</summary>
    public const string UserAccountId = "user.account_id";

    /// <summary>Organization identifier.</summary>
    public const string OrganizationId = "organization.id";

    /// <summary>Terminal type (VS Code, iTerm, etc.).</summary>
    public const string TerminalType = "terminal.type";

    /// <summary>Query source name.</summary>
    public const string QuerySource = "query_source";

    /// <summary>Model name used for the request.</summary>
    public const string Model = "model";

    /// <summary>User prompt text.</summary>
    public const string Prompt = "prompt";

    /// <summary>User prompt text length.</summary>
    public const string PromptLength = "prompt_length";

    /// <summary>Prompt group identifier (clusters related events).</summary>
    public const string PromptId = "prompt.id";

    /// <summary>Per-completion API request identifier.</summary>
    public const string RequestId = "request_id";

    /// <summary>Tool use correlation identifier.</summary>
    public const string ToolUseId = "tool_use_id";

    /// <summary>Tool name.</summary>
    public const string ToolName = "tool_name";

    /// <summary>Tool parameters.</summary>
    public const string ToolParameters = "tool_parameters";

    /// <summary>Tool input.</summary>
    public const string ToolInput = "tool_input";

    /// <summary>MCP server name.</summary>
    public const string McpServerName = "mcp_server_name";

    /// <summary>MCP tool name.</summary>
    public const string McpToolName = "mcp_tool_name";

    /// <summary>MCP server scope.</summary>
    public const string McpServerScope = "mcp_server_scope";

    /// <summary>Duration in milliseconds.</summary>
    public const string DurationMs = "duration_ms";

    /// <summary>Whether the operation succeeded.</summary>
    public const string Success = "success";

    /// <summary>Tool approval decision (accept or reject).</summary>
    public const string Decision = "decision";

    /// <summary>Source of the tool approval decision.</summary>
    public const string DecisionSource = "source";

    /// <summary>Input token count.</summary>
    public const string InputTokens = "input_tokens";

    /// <summary>Output token count.</summary>
    public const string OutputTokens = "output_tokens";

    /// <summary>Cache read token count.</summary>
    public const string CacheReadTokens = "cache_read_tokens";

    /// <summary>Cache creation token count.</summary>
    public const string CacheCreationTokens = "cache_creation_tokens";

    /// <summary>Cost in USD.</summary>
    public const string CostUsd = "cost_usd";

    /// <summary>Cost in micro-USD.</summary>
    public const string CostUsdMicros = "cost_usd_micros";

    /// <summary>
    /// Checks whether a body string is one of the Claude Code events this adapter handles.
    /// </summary>
    public static bool IsClaudeCodeEventName(string? eventName) =>
        eventName
            is UserPromptEventName
                or ApiRequestEventName
                or ToolResultEventName
                or ToolDecisionEventName
                or McpServerConnectionEventName;

    /// <summary>
    /// Checks whether an event is one of the Claude Code events Zeeq stores (kept events).
    /// </summary>
    public static bool IsKeptEventName(string? eventName) =>
        eventName
            is UserPromptEventName
                or ApiRequestEventName
                or ToolResultEventName
                or ToolDecisionEventName;
}
