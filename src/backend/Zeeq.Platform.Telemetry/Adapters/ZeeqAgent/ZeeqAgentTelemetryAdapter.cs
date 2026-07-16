using System.Globalization;
using System.Text.Json;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Adapters.ZeeqAgent;

/// <summary>
/// Adapts first-party JSON telemetry imports after they are mapped into OTLP logs.
/// </summary>
/// <remarks>
/// The adapter treats <c>zeeq-agent</c> as an internal transport identity while
/// preserving the caller-reported harness name on the normalized conversation.
/// </remarks>
public sealed class ZeeqAgentTelemetryAdapter : AgentTelemetryAdapterBase, ITelemetryLogFilter
{
    private const int MaxOutputSnippetLength = 16 * 1024;

    /// <inheritdoc />
    public override string HarnessName => ZeeqAgentLogAttributes.ServiceName;

    /// <inheritdoc />
    public bool HandlesService(string serviceName) =>
        string.Equals(serviceName, ZeeqAgentLogAttributes.ServiceName, StringComparison.Ordinal);

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

        return attrs.ContainsKey(ZeeqAgentLogAttributes.ConversationId)
            && attrs.TryGetValue(ZeeqAgentLogAttributes.EventKind, out var kind)
            && IsKnownKind(kind.StringValue);
    }

    /// <inheritdoc />
    public override bool CanHandle(TelemetryLogRecordContext record) =>
        HandlesService(record.ServiceName)
        && !string.IsNullOrWhiteSpace(record.LogString(ZeeqAgentLogAttributes.ConversationId))
        && IsKnownKind(record.LogString(ZeeqAgentLogAttributes.EventKind));

    /// <inheritdoc />
    public override AgentTelemetryAdapterResult Adapt(TelemetryLogRecordContext record)
    {
        var conversationId = record.LogString(ZeeqAgentLogAttributes.ConversationId) ?? "";
        var obs = new AgentConversationObservation
        {
            ConversationId = conversationId,
            OrganizationId = "",
            Harness =
                EmptyToNull(record.LogString(ZeeqAgentLogAttributes.HarnessName)) ?? HarnessName,
            AppVersion = EmptyToNull(record.LogString(ZeeqAgentLogAttributes.HarnessVersion)),
            RepoRemoteUrl = EmptyToNull(
                record.LogString(ZeeqAgentLogAttributes.RepositoryRemoteUrl)
            ),
            HeadBranch = EmptyToNull(record.LogString(ZeeqAgentLogAttributes.HeadBranch)),
            HeadSha = EmptyToNull(record.LogString(ZeeqAgentLogAttributes.HeadSha)),
            OwnerEmail = EmptyToNull(record.LogString(ZeeqAgentLogAttributes.UserEmail)),
            Model = EmptyToNull(record.LogString(ZeeqAgentLogAttributes.Model)),
        };

        var occurredAtUtc = record.ReadEventTimestampUtc(ZeeqAgentLogAttributes.EventTimestamp);
        var eventId = EmptyToNull(record.LogString(ZeeqAgentLogAttributes.EventId));
        var evt = record.LogString(ZeeqAgentLogAttributes.EventKind) switch
        {
            "prompt" => AdaptPrompt(record, occurredAtUtc, eventId),
            "tool_result" => AdaptToolResult(record, occurredAtUtc, eventId),
            "completion" => AdaptCompletion(record, occurredAtUtc, eventId),
            _ => null,
        };

        return evt is not null
            ? AgentTelemetryAdapterResult.WithEvent(obs, evt)
            : AgentTelemetryAdapterResult.ConversationOnly(obs);
    }

    private static AgentSessionEventRecord AdaptPrompt(
        TelemetryLogRecordContext record,
        DateTimeOffset occurredAtUtc,
        string? eventId
    ) =>
        new(
            EventType: AgentSessionEventType.Prompt,
            PromptText: EmptyToNull(record.LogString(ZeeqAgentLogAttributes.PromptText)),
            PromptLength: record.LogInt32(ZeeqAgentLogAttributes.PromptLength),
            Model: EmptyToNull(record.LogString(ZeeqAgentLogAttributes.Model)),
            OccurredAtUtc: occurredAtUtc,
            SourceRecordId: eventId
        );

    private static AgentSessionEventRecord AdaptToolResult(
        TelemetryLogRecordContext record,
        DateTimeOffset occurredAtUtc,
        string? eventId
    )
    {
        var output = EmptyToNull(record.LogString(ZeeqAgentLogAttributes.ToolResult));

        return new(
            EventType: AgentSessionEventType.ToolResult,
            ToolName: EmptyToNull(record.LogString(ZeeqAgentLogAttributes.ToolName)),
            ToolNameRaw: EmptyToNull(record.LogString(ZeeqAgentLogAttributes.ToolName)),
            McpServer: EmptyToNull(record.LogString(ZeeqAgentLogAttributes.McpServerName)),
            ArgumentsJson: ParseArguments(record.LogString(ZeeqAgentLogAttributes.ToolArguments)),
            OutputSnippet: output is { Length: > MaxOutputSnippetLength }
                ? output[..MaxOutputSnippetLength]
                : output,
            Success: record.LogBool(ZeeqAgentLogAttributes.ToolSucceeded),
            DurationMs: record.LogInt32(ZeeqAgentLogAttributes.ToolDurationMs),
            OccurredAtUtc: occurredAtUtc,
            SourceRecordId: eventId,
            ToolCallId: EmptyToNull(record.LogString(ZeeqAgentLogAttributes.ToolCallId))
        );
    }

    private static AgentSessionEventRecord AdaptCompletion(
        TelemetryLogRecordContext record,
        DateTimeOffset occurredAtUtc,
        string? eventId
    )
    {
        var costUsd = ReadDecimal(record.LogString(ZeeqAgentLogAttributes.CostUsd));

        return new(
            EventType: AgentSessionEventType.Completion,
            Model: EmptyToNull(record.LogString(ZeeqAgentLogAttributes.Model)),
            InputTokens: record.LogInt32(ZeeqAgentLogAttributes.InputTokens),
            CachedTokens: record.LogInt32(ZeeqAgentLogAttributes.CachedTokens),
            OutputTokens: record.LogInt32(ZeeqAgentLogAttributes.OutputTokens),
            ReasoningTokens: record.LogInt32(ZeeqAgentLogAttributes.ReasoningTokens),
            CostUsd: costUsd,
            CostSource: costUsd.HasValue ? AgentSessionEventCostSource.ReportedUsd : null,
            OccurredAtUtc: occurredAtUtc,
            SourceRecordId: eventId
        );
    }

    private static bool IsKnownKind(string? kind) =>
        kind is "prompt" or "tool_result" or "completion";

    private static JsonDocument? ParseArguments(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
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

    private static decimal? ReadDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
