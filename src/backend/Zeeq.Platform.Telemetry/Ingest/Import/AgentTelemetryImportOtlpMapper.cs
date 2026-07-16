using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Platform.Telemetry.Adapters.ZeeqAgent;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Zeeq.Platform.Telemetry.Ingest.Import;

/// <summary>
/// Maps JSON import requests into the internal <c>zeeq-agent</c> OTLP log shape.
/// </summary>
public sealed class AgentTelemetryImportOtlpMapper
{
    /// <summary>
    /// Serializes a JSON import request as an OTLP logs export payload.
    /// </summary>
    public byte[] Map(AgentTelemetryImportRequest request)
    {
        var export = ToOtlpLogsRequest(request);

        return export.ToByteArray();
    }

    /// <summary>
    /// Converts a JSON import request to an OTLP logs export request.
    /// </summary>
    public ExportLogsServiceRequest ToOtlpLogsRequest(AgentTelemetryImportRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = new ScopeLogs
        {
            Scope = new InstrumentationScope { Name = ZeeqAgentLogAttributes.ServiceName },
        };

        for (var i = 0; i < request.Events.Count; i++)
        {
            var item = request.Events[i];
            var occurredAtUtc = item.OccurredAtUtc ?? now;
            var sourceRecordId = string.IsNullOrWhiteSpace(item.EventId)
                ? DerivedEventId(request, item, i)
                : item.EventId;
            var record = new LogRecord
            {
                Body = new AnyValue { StringValue = EventName(item.Kind) },
                TimeUnixNano = ToUnixNanoseconds(occurredAtUtc),
            };

            Add(record, ZeeqAgentLogAttributes.ConversationId, request.ConversationId);
            Add(record, ZeeqAgentLogAttributes.HarnessName, request.HarnessName);
            Add(record, ZeeqAgentLogAttributes.HarnessVersion, request.HarnessVersion);
            Add(record, ZeeqAgentLogAttributes.RepositoryRemoteUrl, request.RepositoryRemoteUrl);
            Add(record, ZeeqAgentLogAttributes.HeadBranch, request.HeadBranch);
            Add(record, ZeeqAgentLogAttributes.HeadSha, request.HeadSha);
            Add(record, ZeeqAgentLogAttributes.EventKind, EventKindValue(item.Kind));
            Add(record, ZeeqAgentLogAttributes.EventId, sourceRecordId);
            Add(record, ZeeqAgentLogAttributes.EventTimestamp, occurredAtUtc.ToString("O"));
            Add(record, ZeeqAgentLogAttributes.PromptText, item.PromptText);
            Add(record, ZeeqAgentLogAttributes.PromptLength, item.PromptLength);
            Add(record, ZeeqAgentLogAttributes.ToolName, item.ToolName);
            Add(record, ZeeqAgentLogAttributes.ToolCallId, item.ToolCallId);
            Add(record, ZeeqAgentLogAttributes.McpServerName, item.McpServerName);
            Add(record, ZeeqAgentLogAttributes.ToolArguments, item.ToolArguments?.GetRawText());
            Add(record, ZeeqAgentLogAttributes.ToolResult, item.ToolResult);
            Add(record, ZeeqAgentLogAttributes.ToolDurationMs, item.ToolDurationMs);
            Add(record, ZeeqAgentLogAttributes.ToolSucceeded, item.ToolSucceeded);
            Add(record, ZeeqAgentLogAttributes.Model, item.Model);
            Add(record, ZeeqAgentLogAttributes.InputTokens, item.InputTokens);
            Add(record, ZeeqAgentLogAttributes.CachedTokens, item.CachedTokens);
            Add(record, ZeeqAgentLogAttributes.OutputTokens, item.OutputTokens);
            Add(record, ZeeqAgentLogAttributes.ReasoningTokens, item.ReasoningTokens);
            Add(record, ZeeqAgentLogAttributes.CostUsd, item.CostUsd);
            Add(record, ZeeqAgentLogAttributes.UserEmail, item.UserEmail);
            Add(record, ZeeqAgentLogAttributes.OrganizationId, item.OrganizationId);

            scope.LogRecords.Add(record);
        }

        var resource = new Resource();
        resource.Attributes.Add(Attribute("service.name", ZeeqAgentLogAttributes.ServiceName));

        var logs = new ResourceLogs { Resource = resource };
        logs.ScopeLogs.Add(scope);

        var export = new ExportLogsServiceRequest();
        export.ResourceLogs.Add(logs);

        return export;
    }

    private static void Add(LogRecord record, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            record.Attributes.Add(Attribute(key, value));
        }
    }

    private static void Add(LogRecord record, string key, int? value)
    {
        if (value.HasValue)
        {
            record.Attributes.Add(
                new KeyValue
                {
                    Key = key,
                    Value = new AnyValue { IntValue = value.Value },
                }
            );
        }
    }

    private static void Add(LogRecord record, string key, bool? value)
    {
        if (value.HasValue)
        {
            record.Attributes.Add(
                new KeyValue
                {
                    Key = key,
                    Value = new AnyValue { BoolValue = value.Value },
                }
            );
        }
    }

    private static void Add(LogRecord record, string key, decimal? value)
    {
        if (value.HasValue)
        {
            record.Attributes.Add(
                Attribute(key, value.Value.ToString(CultureInfo.InvariantCulture))
            );
        }
    }

    private static KeyValue Attribute(string key, string value) =>
        new()
        {
            Key = key,
            Value = new AnyValue { StringValue = value },
        };

    private static string EventName(AgentEventKind kind) =>
        kind switch
        {
            AgentEventKind.Prompt => "zeeq_agent.prompt",
            AgentEventKind.ToolResult => "zeeq_agent.tool_result",
            AgentEventKind.Completion => "zeeq_agent.completion",
            _ => "zeeq_agent.unknown",
        };

    private static string EventKindValue(AgentEventKind kind) =>
        kind switch
        {
            AgentEventKind.Prompt => "prompt",
            AgentEventKind.ToolResult => "tool_result",
            AgentEventKind.Completion => "completion",
            _ => "unknown",
        };

    private static string DerivedEventId(
        AgentTelemetryImportRequest request,
        ImportedAgentEvent item,
        int index
    )
    {
        var material = JsonSerializer.Serialize(
            new
            {
                request.ConversationId,
                Index = index,
                item.Kind,
                item.OccurredAtUtc,
                item.PromptText,
                item.PromptLength,
                item.ToolName,
                item.ToolCallId,
                item.McpServerName,
                ToolArguments = item.ToolArguments?.GetRawText(),
                item.ToolResult,
                item.Model,
                item.InputTokens,
                item.CachedTokens,
                item.OutputTokens,
                item.ReasoningTokens,
            }
        );
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return $"import:{Convert.ToHexString(bytes)[..32].ToLowerInvariant()}";
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset timestamp)
    {
        var unixNanoseconds =
            timestamp.ToUnixTimeMilliseconds() * 1_000_000L
            + (timestamp.Ticks % TimeSpan.TicksPerMillisecond) * 100L;

        return (ulong)Math.Max(0, unixNanoseconds);
    }
}
