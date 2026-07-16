namespace Zeeq.Platform.Telemetry.Adapters.ZeeqAgent;

/// <summary>
/// OTLP log attributes used by Zeeq's first-party JSON telemetry import.
/// </summary>
public static class ZeeqAgentLogAttributes
{
    /// <summary>Internal service name used for JSON imports after OTLP mapping.</summary>
    public const string ServiceName = "zeeq-agent";

    /// <summary>Conversation identifier from the import request.</summary>
    public const string ConversationId = "gen_ai.conversation.id";

    /// <summary>Original source harness name.</summary>
    public const string HarnessName = "zeeq.harness.name";

    /// <summary>Original source harness version.</summary>
    public const string HarnessVersion = "zeeq.harness.version";

    /// <summary>Repository remote URL.</summary>
    public const string RepositoryRemoteUrl = "repository.remote_url";

    /// <summary>Repository head branch.</summary>
    public const string HeadBranch = "repository.head.branch";

    /// <summary>Repository head SHA.</summary>
    public const string HeadSha = "repository.head.sha";

    /// <summary>Event kind discriminator.</summary>
    public const string EventKind = "event.kind";

    /// <summary>Stable source event identifier.</summary>
    public const string EventId = "event.id";

    /// <summary>Event timestamp.</summary>
    public const string EventTimestamp = "event.timestamp";

    /// <summary>Prompt text.</summary>
    public const string PromptText = "prompt_text";

    /// <summary>Prompt length in characters.</summary>
    public const string PromptLength = "prompt_length";

    /// <summary>Tool name.</summary>
    public const string ToolName = "gen_ai.tool.name";

    /// <summary>Tool call identifier.</summary>
    public const string ToolCallId = "gen_ai.tool.call.id";

    /// <summary>MCP server name.</summary>
    public const string McpServerName = "mcp.server.name";

    /// <summary>Tool call arguments as JSON text.</summary>
    public const string ToolArguments = "gen_ai.tool.call.arguments";

    /// <summary>Tool call result or output snippet.</summary>
    public const string ToolResult = "gen_ai.tool.call.result";

    /// <summary>Tool call duration in milliseconds.</summary>
    public const string ToolDurationMs = "gen_ai.tool.call.duration_ms";

    /// <summary>Whether the tool call succeeded.</summary>
    public const string ToolSucceeded = "gen_ai.tool.call.success";

    /// <summary>Model name.</summary>
    public const string Model = "gen_ai.request.model";

    /// <summary>Input token count.</summary>
    public const string InputTokens = "gen_ai.usage.input_tokens";

    /// <summary>Cached token count.</summary>
    public const string CachedTokens = "gen_ai.usage.cached_tokens";

    /// <summary>Output token count.</summary>
    public const string OutputTokens = "gen_ai.usage.output_tokens";

    /// <summary>Reasoning token count.</summary>
    public const string ReasoningTokens = "gen_ai.usage.reasoning_tokens";

    /// <summary>Reported cost in USD.</summary>
    public const string CostUsd = "zeeq.cost.usd";

    /// <summary>Observed source user email.</summary>
    public const string UserEmail = "user.email";

    /// <summary>Observed source organization id.</summary>
    public const string OrganizationId = "organization_id";
}
