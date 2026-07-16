using System.Text.Json;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Adapters.Copilot;

/// <summary>
/// Adapter for Copilot Chat spans. Identifies key business spans (<c>invoke_agent</c>,
/// <c>chat</c>, <c>execute_tool</c>) and adapts them to the conversation/event model.
/// </summary>
/// <remarks>
/// Key rules:
/// - <c>invoke_agent</c>: prompt row + conversation metadata (repo/git context, model).
/// - <c>chat</c>: completion row (tokens, cost). Child of <c>invoke_agent</c> — never
///   use <c>invoke_agent</c>'s aggregated tokens (double-count).
/// - <c>execute_tool</c> with <c>type=="extension"</c>: tool_result row. Drop built-ins
///   (<c>type=="function"</c>).
/// - Drop: spans with neither a session id nor complete repository/branch context.
///
/// Log records are not ingested for Copilot; the adapter returns false for
/// <c>CanHandle</c> on log contexts.
/// </remarks>
public sealed class CopilotChatTelemetryAdapter : AgentTelemetryAdapterBase, ITelemetrySpanFilter
{
    private const int MaxOutputSnippetLength = 16 * 1024;

    /// <inheritdoc />
    public bool ShouldKeepSpan(OpenTelemetry.Proto.Trace.V1.Span span, string serviceName)
    {
        if (serviceName != CopilotChatSpanAttributes.HarnessName)
        {
            return false;
        }

        var operationName = span
            .Attributes.FirstOrDefault(a => a.Key == CopilotChatSpanAttributes.GenAiOperationName)
            ?.Value?.StringValue;

        if (operationName is not ("invoke_agent" or "chat" or "execute_tool"))
        {
            return false;
        }

        var chatSessionId = span
            .Attributes.FirstOrDefault(a => a.Key == CopilotChatSpanAttributes.ChatSessionId)
            ?.Value?.StringValue;
        var repositoryRemoteUrl = span
            .Attributes.FirstOrDefault(a => a.Key == CopilotChatSpanAttributes.CopilotRepoRemoteUrl)
            ?.Value?.StringValue;
        var headBranch = span
            .Attributes.FirstOrDefault(a =>
                a.Key == CopilotChatSpanAttributes.CopilotRepoHeadBranch
            )
            ?.Value?.StringValue;

        return TelemetryRepositoryIdentity.ResolveConversationId(
            chatSessionId,
            repositoryRemoteUrl,
            headBranch
        )
            is not null;
    }

    /// <inheritdoc />
    public override string HarnessName => CopilotChatSpanAttributes.HarnessName;

    /// <inheritdoc />
    public override bool CanHandle(TelemetryLogRecordContext record) => false;

    /// <inheritdoc />
    public override AgentTelemetryAdapterResult Adapt(TelemetryLogRecordContext record) =>
        AgentTelemetryAdapterResult.ConversationOnly(
            new()
            {
                ConversationId = "",
                OrganizationId = "",
                Harness = HarnessName,
            }
        );

    /// <inheritdoc />
    public override bool CanHandle(TelemetrySpanRecordContext record) =>
        record.ServiceName == CopilotChatSpanAttributes.HarnessName
        && TelemetryRepositoryIdentity.ResolveConversationId(
            record.ChatSessionId,
            record.SpanString(CopilotChatSpanAttributes.CopilotRepoRemoteUrl),
            record.SpanString(CopilotChatSpanAttributes.CopilotRepoHeadBranch)
        )
            is not null
        && record.OperationName is "invoke_agent" or "chat" or "execute_tool";

    /// <inheritdoc />
    public override AgentTelemetryAdapterResult Adapt(TelemetrySpanRecordContext record)
    {
        var obs = new AgentConversationObservation
        {
            ConversationId = TelemetryRepositoryIdentity.ResolveConversationId(
                record.ChatSessionId,
                record.SpanString(CopilotChatSpanAttributes.CopilotRepoRemoteUrl),
                record.SpanString(CopilotChatSpanAttributes.CopilotRepoHeadBranch)
            )!,
            OrganizationId =
                EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GithubCopilotOrg)) ?? "",
            Harness = CopilotChatSpanAttributes.HarnessName,
            RepoRemoteUrl = EmptyToNull(
                record.SpanString(CopilotChatSpanAttributes.CopilotRepoRemoteUrl)
            ),
            HeadBranch = EmptyToNull(
                record.SpanString(CopilotChatSpanAttributes.CopilotRepoHeadBranch)
            ),
            HeadSha = EmptyToNull(record.SpanString(CopilotChatSpanAttributes.CopilotRepoHeadSha)),
            Model =
                EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiResponseModel))
                ?? EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiRequestModel)),
        };

        var evt = record.OperationName switch
        {
            "invoke_agent" => AdaptInvokeAgent(record),
            "chat" => AdaptCompletion(record),
            "execute_tool" when IsExtensionTool(record) => AdaptToolResult(record),
            _ => null,
        };

        return evt is not null
            ? AgentTelemetryAdapterResult.WithEvent(obs, evt)
            : AgentTelemetryAdapterResult.ConversationOnly(obs);
    }

    private static AgentSessionEventRecord AdaptInvokeAgent(TelemetrySpanRecordContext record)
    {
        var promptText = EmptyToNull(
            record.SpanString(CopilotChatSpanAttributes.CopilotUserRequest)
        );

        var agentName = record.SpanString(CopilotChatSpanAttributes.GenAiAgentName);

        return new(
            EventType: AgentSessionEventType.Prompt,
            SourceRecordId: $"{record.TraceId}:{record.SpanId}",
            PromptText: promptText,
            PromptLength: promptText?.Length,
            Model: EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiRequestModel))
                ?? EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiResponseModel)),
            QuerySource: EmptyToNull(agentName),
            IsHousekeeping: agentName == "progressMessages",
            PromptGroupId: record.TraceId,
            OccurredAtUtc: record.StartedAtUtc
        );
    }

    private static AgentSessionEventRecord AdaptCompletion(TelemetrySpanRecordContext record)
    {
        var inputTokens = record.SpanInt32(CopilotChatSpanAttributes.GenAiUsageInputTokens);
        var outputTokens = record.SpanInt32(CopilotChatSpanAttributes.GenAiUsageOutputTokens);
        var cacheRead =
            record.SpanInt32(CopilotChatSpanAttributes.GenAiUsageCacheReadInputTokens) ?? 0;
        var cacheCreate =
            record.SpanInt32(CopilotChatSpanAttributes.GenAiUsageCacheCreationInputTokens) ?? 0;
        var cachedTokens = cacheRead + cacheCreate;
        var reasoningTokens = record.SpanInt32(
            CopilotChatSpanAttributes.GenAiUsageReasoningOutputTokens
        );
        var aiu = record.SpanInt64(CopilotChatSpanAttributes.CopilotUsageNanoAiu);
        var agentName = record.SpanString(CopilotChatSpanAttributes.GenAiAgentName);

        return new(
            EventType: AgentSessionEventType.Completion,
            SourceRecordId: $"{record.TraceId}:{record.SpanId}",
            Model: EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiResponseModel))
                ?? EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiRequestModel)),
            InputTokens: inputTokens,
            CachedTokens: cachedTokens > 0 ? cachedTokens : null,
            OutputTokens: outputTokens,
            ReasoningTokens: reasoningTokens,
            CostUnitsRaw: aiu,
            QuerySource: EmptyToNull(agentName),
            IsHousekeeping: aiu == 0 || agentName == "progressMessages",
            PromptGroupId: record.TraceId,
            ProviderRequestId: EmptyToNull(
                record.SpanString(CopilotChatSpanAttributes.GenAiResponseId)
            ),
            OccurredAtUtc: record.StartedAtUtc
        );
    }

    private static AgentSessionEventRecord AdaptToolResult(TelemetrySpanRecordContext record)
    {
        var toolName = EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiToolName));
        var serverName = EmptyToNull(record.SpanString(CopilotChatSpanAttributes.McpServerName));
        var normalizedName = NormalizeToolName(serverName, toolName);
        var arguments = EmptyToNull(
            record.SpanString(CopilotChatSpanAttributes.GenAiToolCallArguments)
        );
        var output = EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiToolCallResult));

        return new(
            EventType: AgentSessionEventType.ToolResult,
            SourceRecordId: $"{record.TraceId}:{record.SpanId}",
            ToolNameRaw: toolName,
            ToolName: normalizedName,
            McpServer: serverName,
            ArgumentsJson: arguments is not null ? JsonDocument.Parse(arguments) : null,
            OutputSnippet: output is { Length: > MaxOutputSnippetLength }
                ? output[..MaxOutputSnippetLength]
                : output,
            Success: record.SpanStatus == OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Ok,
            DurationMs: (int)(record.DurationNanos / 1_000_000),
            ToolCallId: EmptyToNull(record.SpanString(CopilotChatSpanAttributes.GenAiToolCallId)),
            PromptGroupId: record.TraceId,
            OccurredAtUtc: record.StartedAtUtc
        );
    }

    private static bool IsExtensionTool(TelemetrySpanRecordContext record) =>
        string.Equals(
            record.SpanString(CopilotChatSpanAttributes.GenAiToolType),
            "extension",
            StringComparison.Ordinal
        );

    private static string? NormalizeToolName(string? serverName, string? toolName)
    {
        if (serverName is null || toolName is null)
        {
            return null;
        }

        return $"mcp__{serverName.Replace("-", "_")}__{toolName}";
    }
}
