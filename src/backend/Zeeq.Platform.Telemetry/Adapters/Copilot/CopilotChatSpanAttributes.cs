namespace Zeeq.Platform.Telemetry.Adapters.Copilot;

/// <summary>
/// Copilot Chat OTLP span attribute names used by telemetry ingestion.
/// </summary>
public static class CopilotChatSpanAttributes
{
    /// <summary>Harness identifier for Copilot Chat.</summary>
    public const string HarnessName = "copilot-chat";

    /// <summary>OTLP service name attribute.</summary>
    public const string ServiceName = "service.name";

    /// <summary>Copilot Chat session identifier.</summary>
    public const string ChatSessionId = "copilot_chat.chat_session_id";

    /// <summary>GenAI operation name (e.g. <c>chat</c>).</summary>
    public const string GenAiOperationName = "gen_ai.operation.name";

    /// <summary>GenAI agent name.</summary>
    public const string GenAiAgentName = "gen_ai.agent.name";

    /// <summary>GenAI request model.</summary>
    public const string GenAiRequestModel = "gen_ai.request.model";

    /// <summary>GenAI response model.</summary>
    public const string GenAiResponseModel = "gen_ai.response.model";

    /// <summary>GenAI response identifier.</summary>
    public const string GenAiResponseId = "gen_ai.response.id";

    /// <summary>GenAI tool name.</summary>
    public const string GenAiToolName = "gen_ai.tool.name";

    /// <summary>GenAI tool type.</summary>
    public const string GenAiToolType = "gen_ai.tool.type";

    /// <summary>GenAI tool call correlation identifier.</summary>
    public const string GenAiToolCallId = "gen_ai.tool.call.id";

    /// <summary>GenAI tool call arguments.</summary>
    public const string GenAiToolCallArguments = "gen_ai.tool.call.arguments";

    /// <summary>GenAI tool call result.</summary>
    public const string GenAiToolCallResult = "gen_ai.tool.call.result";

    /// <summary>GenAI input token count.</summary>
    public const string GenAiUsageInputTokens = "gen_ai.usage.input_tokens";

    /// <summary>GenAI output token count.</summary>
    public const string GenAiUsageOutputTokens = "gen_ai.usage.output_tokens";

    /// <summary>GenAI cache read input token count.</summary>
    public const string GenAiUsageCacheReadInputTokens = "gen_ai.usage.cache_read.input_tokens";

    /// <summary>GenAI cache creation input token count.</summary>
    public const string GenAiUsageCacheCreationInputTokens =
        "gen_ai.usage.cache_creation.input_tokens";

    /// <summary>GenAI reasoning output token count.</summary>
    public const string GenAiUsageReasoningOutputTokens = "gen_ai.usage.reasoning.output_tokens";

    /// <summary>Copilot usage in nano-AIU billing units.</summary>
    public const string CopilotUsageNanoAiu = "copilot_chat.copilot_usage_nano_aiu";

    /// <summary>Copilot user request text.</summary>
    public const string CopilotUserRequest = "copilot_chat.user_request";

    /// <summary>Copilot repository remote URL.</summary>
    public const string CopilotRepoRemoteUrl = "copilot_chat.repo.remote_url";

    /// <summary>Copilot repository head branch.</summary>
    public const string CopilotRepoHeadBranch = "copilot_chat.repo.head.branch";

    /// <summary>Copilot repository head SHA.</summary>
    public const string CopilotRepoHeadSha = "copilot_chat.repo.head.sha";

    /// <summary>GitHub Copilot organization slug.</summary>
    public const string GithubCopilotOrg = "github.copilot.github.org";

    /// <summary>MCP server name from tool parameters.</summary>
    public const string McpServerName = "github.copilot.tool.parameters.mcp_server_name";
}
