using System.Text.Json;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Adapters.Codex;

/// <summary>
/// Converts accepted Codex OTLP log records into v2 unified domain artifacts.
/// </summary>
/// <remarks>
/// Critical fix from v1: <c>IsCodexConversationStarted()</c> no longer checks
/// <c>event.name</c> (which never appears on the wire). Instead, it uses the
/// two-session-validated fallback: both <c>mcp_servers</c> and <c>approval_policy</c>
/// must be present.
///
/// v2 also adds: unified event rows, <c>is_housekeeping</c> flag for priming
/// completions, <c>call_id</c> / <c>logId</c> persistence, and <c>Harness</c> /
/// <c>HarnessVariant</c> / <c>AppVersion</c> fields.
/// </remarks>
public sealed class CodexTelemetryAdapter : AgentTelemetryAdapterBase, ITelemetryLogFilter
{
    private const string HelpfulAssistantPromptPrefix =
        "You are a helpful assistant. You will be presented with a user prompt";
    private const string SafetyCompliancePromptPrefix =
        "You are an expert at upholding safety and compliance standards for Codex ambient suggestions";
    private const string HyperpersonalizedSuggestionsPrompt =
        "Generate 0 to 3 hyperpersonalized suggestions";
    private const string AmbientSuggestionsPromptPrefix =
        "Generate 0 to 3 ambient suggestions for this local project";

    private static readonly string[] ToolNameIncludeFilters = ["mcp__"];

    /// <inheritdoc />
    public bool HandlesService(string serviceName) =>
        serviceName.Contains("codex", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool ShouldKeepLogRecord(
        OpenTelemetry.Proto.Logs.V1.LogRecord record,
        string serviceName
    )
    {
        if (!HandlesService(serviceName))
        {
            return false;
        }

        var attrs = record.Attributes.ToDictionary(a => a.Key, a => a.Value);

        return attrs.ContainsKey("prompt_length")
            || (attrs.ContainsKey("tool_name") && attrs.ContainsKey("call_id"))
            || string.Equals(
                record.Attributes.FirstOrDefault(a => a.Key == "event.kind")?.Value?.StringValue,
                "response.completed",
                StringComparison.Ordinal
            )
            || (attrs.ContainsKey("mcp_servers") && attrs.ContainsKey("approval_policy"));
    }

    /// <inheritdoc />
    public override string HarnessName => CodexLogAttributes.HarnessName;

    /// <inheritdoc />
    public override bool CanHandle(TelemetryLogRecordContext record) =>
        record.LooksLikeCodexSessionRecord();

    /// <inheritdoc />
    public override AgentTelemetryAdapterResult Adapt(TelemetryLogRecordContext record)
    {
        var conversationId = EmptyToNull(record.LogString(CodexLogAttributes.ConversationId));
        if (conversationId is null)
        {
            return AgentTelemetryAdapterResult.ConversationOnly(
                new()
                {
                    ConversationId = "",
                    OrganizationId = "",
                    Harness = HarnessName,
                }
            );
        }

        if (IsExcludedDesktopPrompt(record))
        {
            return AgentTelemetryAdapterResult.ConversationOnly(
                new()
                {
                    ConversationId = conversationId,
                    OrganizationId = "",
                    Harness = HarnessName,
                }
            );
        }

        var occurredAtUtc = record.ReadEventTimestampUtc(CodexLogAttributes.EventTimestamp);
        var logId = EmptyToNull(record.LogString(CodexLogAttributes.LogId));
        var obs = BuildObservation(record, conversationId);

        if (record.IsCodexUserPrompt())
        {
            return AgentTelemetryAdapterResult.WithEvent(
                obs,
                new(
                    EventType: AgentSessionEventType.Prompt,
                    PromptText: EmptyToNull(record.LogString(CodexLogAttributes.Prompt)),
                    PromptLength: record.LogInt32(CodexLogAttributes.PromptLength),
                    OccurredAtUtc: occurredAtUtc,
                    SourceRecordId: logId
                )
            );
        }

        if (record.IsCodexToolResult())
        {
            var toolName = record.LogString(CodexLogAttributes.ToolName);

            if (string.IsNullOrWhiteSpace(toolName))
            {
                return AgentTelemetryAdapterResult.ConversationOnly(obs);
            }

            if (
                !ToolNameIncludeFilters.Any(f =>
                    toolName.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return AgentTelemetryAdapterResult.ConversationOnly(obs);
            }

            return AgentTelemetryAdapterResult.WithEvent(
                obs,
                new(
                    EventType: AgentSessionEventType.ToolResult,
                    ToolName: toolName,
                    ToolNameRaw: toolName,
                    ArgumentsJson: ParseArguments(record.LogString(CodexLogAttributes.Arguments)),
                    OutputSnippet: Truncate(
                        EmptyToNull(record.LogString(CodexLogAttributes.Output))
                    ),
                    McpServer: EmptyToNull(record.LogString(CodexLogAttributes.McpServer)),
                    McpServerOrigin: EmptyToNull(record.LogString(CodexLogAttributes.McpOrigin)),
                    Success: record.LogBool(CodexLogAttributes.Success),
                    DurationMs: record.LogInt32(CodexLogAttributes.DurationMs),
                    OccurredAtUtc: occurredAtUtc,
                    SourceRecordId: logId,
                    ToolCallId: EmptyToNull(record.LogString(CodexLogAttributes.ToolCallId))
                )
            );
        }

        if (record.IsCodexResponseCompleted())
        {
            var outputTokens = record.LogInt32(CodexLogAttributes.OutputTokenCount) ?? 0;
            var inputTokens = record.LogInt32(CodexLogAttributes.InputTokenCount) ?? 0;
            var toolTokens = record.LogInt32(CodexLogAttributes.ToolTokenCount) ?? 0;
            var isHousekeeping = outputTokens == 0 && toolTokens == inputTokens;

            return AgentTelemetryAdapterResult.WithEvent(
                obs,
                new(
                    EventType: AgentSessionEventType.Completion,
                    Model: EmptyToNull(record.LogString(CodexLogAttributes.Model)),
                    InputTokens: inputTokens,
                    CachedTokens: record.LogInt32(CodexLogAttributes.CachedTokenCount),
                    OutputTokens: outputTokens,
                    ReasoningTokens: record.LogInt32(CodexLogAttributes.ReasoningTokenCount),
                    ToolTokens: toolTokens,
                    OccurredAtUtc: occurredAtUtc,
                    SourceRecordId: logId,
                    IsHousekeeping: isHousekeeping
                )
            );
        }

        if (record.IsCodexConversationStarted())
        {
            return AgentTelemetryAdapterResult.ConversationOnly(obs);
        }

        return AgentTelemetryAdapterResult.ConversationOnly(obs);
    }

    private static AgentConversationObservation BuildObservation(
        TelemetryLogRecordContext record,
        string conversationId
    ) =>
        new()
        {
            ConversationId = conversationId,
            OrganizationId = "", // Codex doesn't emit org; filled by ingest principal in processor
            Harness = CodexLogAttributes.HarnessName,
            HarnessVariant =
                EmptyToNull(record.LogString(CodexLogAttributes.Originator))
                ?? EmptyToNull(record.LogString(CodexLogAttributes.TerminalType)),
            AppVersion = EmptyToNull(record.LogString(CodexLogAttributes.AppVersion)),
            OwnerEmail = EmptyToNull(record.LogString(CodexLogAttributes.UserEmail)),
            Model = EmptyToNull(record.LogString(CodexLogAttributes.Model)),
        };

    private static JsonDocument? ParseArguments(string? args)
    {
        if (args is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(args);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsExcludedDesktopPrompt(TelemetryLogRecordContext record)
    {
        if (
            !string.Equals(
                record.ResourceString(CodexLogAttributes.ServiceName),
                CodexLogAttributes.DesktopServiceName,
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        if (
            !string.Equals(
                record.LogString(CodexLogAttributes.Originator),
                CodexLogAttributes.DesktopOriginator,
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        var prompt = record.LogString(CodexLogAttributes.Prompt);

        return prompt?.StartsWith(HelpfulAssistantPromptPrefix, StringComparison.Ordinal) == true
            || prompt?.StartsWith(SafetyCompliancePromptPrefix, StringComparison.Ordinal) == true
            || prompt?.Contains(HyperpersonalizedSuggestionsPrompt, StringComparison.Ordinal)
                == true
            || prompt?.StartsWith(AmbientSuggestionsPromptPrefix, StringComparison.Ordinal) == true
            || (prompt?.StartsWith("Conversation") == true && prompt?.EndsWith('…') == true);
    }

    private static string? Truncate(string? value) =>
        value is { Length: > CodexLogAttributes.MaxToolOutputSnippetLength }
            ? value[..CodexLogAttributes.MaxToolOutputSnippetLength]
            : value;
}
