using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Adapters.ClaudeCode;

/// <summary>
/// Converts accepted Claude Code OTLP log records into v2 unified domain artifacts.
/// </summary>
/// <remarks>
/// v2 changes from v1:
/// - Keeps <c>tool_decision</c> events (v1 dropped them).
/// - Extracts <c>query_source</c> → <c>is_housekeeping</c> flag.
/// - Returns unified <see cref="AgentSessionEventRecord"/> instead of typed slots.
/// - Extracts correlation IDs: <c>prompt.id</c>, <c>tool_use_id</c>, <c>request_id</c>.
/// - Maps <c>event.sequence</c> → <c>SourceSequence</c> and <c>event.timestamp</c> → <c>OccurredAtUtc</c>.
/// </remarks>
public sealed class ClaudeCodeTelemetryAdapter : AgentTelemetryAdapterBase, ITelemetryLogFilter
{
    private const string McpToolName = "mcp_tool";

    /// <inheritdoc />
    public bool HandlesService(string serviceName) =>
        serviceName == ClaudeCodeLogAttributes.HarnessName;

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

        var body = record.Body?.StringValue;

        return body
            is ClaudeCodeLogAttributes.UserPromptEventName
                or ClaudeCodeLogAttributes.ApiRequestEventName
                or ClaudeCodeLogAttributes.ToolResultEventName
                or ClaudeCodeLogAttributes.ToolDecisionEventName;
    }

    /// <inheritdoc />
    public override string HarnessName => ClaudeCodeLogAttributes.HarnessName;

    /// <inheritdoc />
    public override bool CanHandle(TelemetryLogRecordContext record) =>
        record.HasClaudeCodeIdentity()
        && ClaudeCodeLogAttributes.IsClaudeCodeEventName(record.ReadClaudeCodeEventName());

    /// <inheritdoc />
    public override AgentTelemetryAdapterResult Adapt(TelemetryLogRecordContext record)
    {
        if (!CanHandle(record))
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

        var conversationId = EmptyToNull(record.LogString(ClaudeCodeLogAttributes.SessionId));
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

        var eventName = record.ReadClaudeCodeEventName();
        var occurredAtUtc = record.ReadEventTimestampUtc(ClaudeCodeLogAttributes.EventTimestamp);
        var sequence = record.LogInt64(ClaudeCodeLogAttributes.EventSequence);
        var obs = BuildObservation(record, conversationId);

        var evt = eventName switch
        {
            ClaudeCodeLogAttributes.UserPromptEventName => AdaptPrompt(
                record,
                occurredAtUtc,
                sequence
            ),
            ClaudeCodeLogAttributes.ApiRequestEventName => AdaptCompletion(
                record,
                occurredAtUtc,
                sequence
            ),
            ClaudeCodeLogAttributes.ToolResultEventName => AdaptToolResult(
                record,
                occurredAtUtc,
                sequence
            ),
            ClaudeCodeLogAttributes.ToolDecisionEventName => AdaptToolDecision(
                record,
                occurredAtUtc,
                sequence
            ),
            _ => null,
        };

        return evt is not null
            ? AgentTelemetryAdapterResult.WithEvent(obs, evt)
            : AgentTelemetryAdapterResult.ConversationOnly(obs);
    }

    private static AgentConversationObservation BuildObservation(
        TelemetryLogRecordContext record,
        string conversationId
    )
    {
        return new()
        {
            ConversationId = conversationId,
            OrganizationId =
                EmptyToNull(record.LogString(ClaudeCodeLogAttributes.OrganizationId)) ?? "",
            Harness = ClaudeCodeLogAttributes.HarnessName,
            HarnessVariant = EmptyToNull(record.LogString(ClaudeCodeLogAttributes.TerminalType)),
            OwnerEmail = EmptyToNull(record.LogString(ClaudeCodeLogAttributes.UserEmail)),
            Model = EmptyToNull(record.LogString(ClaudeCodeLogAttributes.Model)),
        };
    }

    private static AgentSessionEventRecord AdaptPrompt(
        TelemetryLogRecordContext record,
        DateTimeOffset occurredAtUtc,
        long? sequence
    ) =>
        new(
            EventType: AgentSessionEventType.Prompt,
            PromptText: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.Prompt)),
            PromptLength: record.LogInt32(ClaudeCodeLogAttributes.PromptLength),
            OccurredAtUtc: occurredAtUtc,
            SourceSequence: sequence,
            PromptGroupId: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.PromptId)),
            QuerySource: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.QuerySource)),
            IsHousekeeping: IsHousekeepingQuerySource(
                record.LogString(ClaudeCodeLogAttributes.QuerySource)
            )
        );

    private static AgentSessionEventRecord AdaptCompletion(
        TelemetryLogRecordContext record,
        DateTimeOffset occurredAtUtc,
        long? sequence
    )
    {
        var freshInput = record.LogInt32(ClaudeCodeLogAttributes.InputTokens) ?? 0;
        var cacheRead = record.LogInt32(ClaudeCodeLogAttributes.CacheReadTokens) ?? 0;
        var cacheCreate = record.LogInt32(ClaudeCodeLogAttributes.CacheCreationTokens) ?? 0;
        var cached = Math.Max(cacheRead, 0) + Math.Max(cacheCreate, 0);
        var input = Math.Max(freshInput, 0) + cached;

        return new(
            EventType: AgentSessionEventType.Completion,
            Model: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.Model)),
            InputTokens: input,
            CachedTokens: cached,
            OutputTokens: record.LogInt32(ClaudeCodeLogAttributes.OutputTokens),
            CostUsd: ReadCostUsd(record),
            CostSource: AgentSessionEventCostSource.ReportedUsd,
            OccurredAtUtc: occurredAtUtc,
            SourceSequence: sequence,
            PromptGroupId: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.PromptId)),
            ProviderRequestId: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.RequestId)),
            QuerySource: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.QuerySource)),
            IsHousekeeping: IsHousekeepingQuerySource(
                record.LogString(ClaudeCodeLogAttributes.QuerySource)
            )
        );
    }

    private static AgentSessionEventRecord AdaptToolResult(
        TelemetryLogRecordContext record,
        DateTimeOffset occurredAtUtc,
        long? sequence
    )
    {
        var rawToolName = EmptyToNull(record.LogString(ClaudeCodeLogAttributes.ToolName));
        if (
            rawToolName is null
            || !string.Equals(rawToolName, McpToolName, StringComparison.Ordinal)
        )
        {
            return null!;
        }

        var toolParameters = EmptyToNull(record.LogString(ClaudeCodeLogAttributes.ToolParameters));
        var serverName = ReadJsonString(toolParameters, ClaudeCodeLogAttributes.McpServerName);
        var toolName = ReadJsonString(toolParameters, ClaudeCodeLogAttributes.McpToolName);
        var normalized =
            serverName is not null && toolName is not null
                ? $"mcp__{serverName}__{toolName}"
                : rawToolName;

        return new(
            EventType: AgentSessionEventType.ToolResult,
            ToolName: normalized,
            ToolNameRaw: rawToolName,
            McpServer: serverName,
            McpServerScope: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.McpServerScope)),
            ArgumentsJson: BuildArgumentsJson(
                toolParameters,
                EmptyToNull(record.LogString(ClaudeCodeLogAttributes.ToolInput))
            ),
            Success: record.LogBool(ClaudeCodeLogAttributes.Success),
            DurationMs: record.LogInt32(ClaudeCodeLogAttributes.DurationMs),
            OccurredAtUtc: occurredAtUtc,
            SourceSequence: sequence,
            ToolCallId: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.ToolUseId)),
            PromptGroupId: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.PromptId))
        );
    }

    private static AgentSessionEventRecord AdaptToolDecision(
        TelemetryLogRecordContext record,
        DateTimeOffset occurredAtUtc,
        long? sequence
    )
    {
        var querySource = record.LogString(ClaudeCodeLogAttributes.QuerySource);

        return new(
            EventType: AgentSessionEventType.ToolDecision,
            ToolNameRaw: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.ToolName)),
            ToolCallId: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.ToolUseId)),
            Decision: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.Decision)),
            DecisionSource: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.DecisionSource)),
            QuerySource: EmptyToNull(querySource),
            IsHousekeeping: IsHousekeepingQuerySource(querySource),
            PromptGroupId: EmptyToNull(record.LogString(ClaudeCodeLogAttributes.PromptId)),
            OccurredAtUtc: occurredAtUtc,
            SourceSequence: sequence
        );
    }

    /// <summary>
    /// Marks non-main-thread query sources as housekeeping. <c>generate_session_title</c>
    /// and similar side-calls should not become the starting prompt or contribute to
    /// housekeeping-excluding rollups.
    /// </summary>
    private static bool IsHousekeepingQuerySource(string? source) =>
        source is not null && source is not "repl_main_thread";

    private static decimal? ReadCostUsd(TelemetryLogRecordContext record)
    {
        if (
            decimal.TryParse(
                record.LogString(ClaudeCodeLogAttributes.CostUsd),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var usd
            )
        )
        {
            return usd;
        }

        if (
            decimal.TryParse(
                record.LogString(ClaudeCodeLogAttributes.CostUsdMicros),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var micros
            )
        )
        {
            return micros / 1_000_000m;
        }

        return null;
    }

    private static JsonDocument? BuildArgumentsJson(string? toolParameters, string? toolInput)
    {
        var obj = new JsonObject();
        if (toolParameters is not null)
        {
            obj[ClaudeCodeLogAttributes.ToolParameters] = ParseJsonValue(toolParameters);
        }

        if (toolInput is not null)
        {
            obj[ClaudeCodeLogAttributes.ToolInput] = ParseJsonValue(toolInput);
        }

        if (obj.Count == 0)
        {
            return null;
        }

        return JsonDocument.Parse(obj.ToJsonString());
    }

    private static JsonNode? ParseJsonValue(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    private static string? ReadJsonString(string? json, string propertyName)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var prop)
                && prop.ValueKind == JsonValueKind.String
                ? EmptyToNull(prop.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
