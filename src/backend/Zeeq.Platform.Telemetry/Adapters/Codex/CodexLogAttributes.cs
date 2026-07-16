namespace Zeeq.Platform.Telemetry.Adapters.Codex;

/// <summary>
/// Codex OTLP attribute, event, and harness names used by telemetry conversion.
/// </summary>
public static class CodexLogAttributes
{
    /// <summary>Harness identifier for Codex.</summary>
    public const string HarnessName = "codex";

    /// <summary>Codex conversation identifier.</summary>
    public const string ConversationId = "conversation.id";

    /// <summary>Event timestamp attribute.</summary>
    public const string EventTimestamp = "event.timestamp";

    /// <summary>OTLP service name attribute.</summary>
    public const string ServiceName = "service.name";

    /// <summary>Expected <c>service.name</c> value for Codex Desktop.</summary>
    public const string DesktopServiceName = "codex-app-server";

    /// <summary>Originator identifier attribute.</summary>
    public const string Originator = "originator";

    /// <summary>Expected originator value for Codex Desktop.</summary>
    public const string DesktopOriginator = "Codex_Desktop";

    /// <summary>Application version.</summary>
    public const string AppVersion = "app.version";

    /// <summary>Terminal type (VS Code, iTerm, etc.).</summary>
    public const string TerminalType = "terminal.type";

    /// <summary>Model name used for the request.</summary>
    public const string Model = "model";

    /// <summary>Reasoning effort setting.</summary>
    public const string ReasoningEffort = "reasoning_effort";

    /// <summary>User email address.</summary>
    public const string UserEmail = "user.email";

    /// <summary>User account identifier.</summary>
    public const string UserAccountId = "user.account_id";

    /// <summary>User prompt text.</summary>
    public const string Prompt = "prompt";

    /// <summary>User prompt text length.</summary>
    public const string PromptLength = "prompt_length";

    /// <summary>Tool name.</summary>
    public const string ToolName = "tool_name";

    /// <summary>Tool call correlation identifier.</summary>
    public const string ToolCallId = "call_id";

    /// <summary>Tool call arguments.</summary>
    public const string Arguments = "arguments";

    /// <summary>Tool call output.</summary>
    public const string Output = "output";

    /// <summary>MCP server name.</summary>
    public const string McpServer = "mcp_server";

    /// <summary>MCP server origin.</summary>
    public const string McpOrigin = "mcp_server_origin";

    /// <summary>Configured MCP servers list.</summary>
    public const string McpServers = "mcp_servers";

    /// <summary>Tool approval policy setting.</summary>
    public const string ApprovalPolicy = "approval_policy";

    /// <summary>Whether the operation succeeded.</summary>
    public const string Success = "success";

    /// <summary>Duration in milliseconds.</summary>
    public const string DurationMs = "duration_ms";

    /// <summary>Input token count.</summary>
    public const string InputTokenCount = "input_token_count";

    /// <summary>Cached token count.</summary>
    public const string CachedTokenCount = "cached_token_count";

    /// <summary>Output token count.</summary>
    public const string OutputTokenCount = "output_token_count";

    /// <summary>Reasoning token count.</summary>
    public const string ReasoningTokenCount = "reasoning_token_count";

    /// <summary>Tool-specific token count.</summary>
    public const string ToolTokenCount = "tool_token_count";

    /// <summary>Codex log identifier for tie-breaking.</summary>
    public const string LogId = "logId";

    /// <summary>Maximum byte length of an output snippet stored in events.</summary>
    public const int MaxToolOutputSnippetLength = 16_384;
}
