using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;
using Zeeq.Platform.Telemetry.Adapters.Codex;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OtlpLog = OpenTelemetry.Proto.Logs.V1;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Codex adapter tests for the accepted user-prompt, completion, tool, and startup shapes.
/// </summary>
[Category("Unit")]
public sealed class CodexTelemetryAdapterTests
{
    private const string ConversationId = "7822f4d1-cd52-4a28-9d7d-1e5f8c0b3a91";

    [Test]
    public async Task Adapt_UserPrompt_CreatesPromptEvent()
    {
        var adapter = new CodexTelemetryAdapter();
        var context = LogContext(
            (CodexLogAttributes.ConversationId, ConversationId),
            (CodexLogAttributes.Prompt, "Explain the result"),
            (CodexLogAttributes.PromptLength, "18"),
            (CodexLogAttributes.EventTimestamp, "2026-07-14T12:00:00Z"),
            (CodexLogAttributes.LogId, "prompt-log")
        );

        await Assert.That(adapter.CanHandle(context)).IsTrue();

        var result = adapter.Adapt(context);

        await Assert.That(result.Conversation.ConversationId).IsEqualTo(ConversationId);
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.Prompt);
        await Assert.That(result.Event.PromptText).IsEqualTo("Explain the result");
        await Assert.That(result.Event.PromptLength).IsEqualTo(18);
        await Assert.That(result.Event.SourceRecordId).IsEqualTo("prompt-log");
    }

    [Test]
    public async Task Adapt_StartupRecord_DoesNotCreatePrompt()
    {
        var adapter = new CodexTelemetryAdapter();
        var context = LogContext(
            (CodexLogAttributes.ConversationId, ConversationId),
            (CodexLogAttributes.McpServers, "[]"),
            (CodexLogAttributes.ApprovalPolicy, "on-request")
        );

        await Assert.That(adapter.CanHandle(context)).IsTrue();
        await Assert.That(adapter.Adapt(context).Event).IsNull();
    }

    [Test]
    public async Task Adapt_McpToolResult_CreatesToolEvent()
    {
        var adapter = new CodexTelemetryAdapter();
        var context = LogContext(
            (CodexLogAttributes.ConversationId, ConversationId),
            (CodexLogAttributes.ToolName, "mcp__zeeq__search_sections"),
            (CodexLogAttributes.ToolCallId, "call-1"),
            (CodexLogAttributes.Arguments, "{\"query\":\"telemetry\"}"),
            (CodexLogAttributes.Output, "result"),
            (CodexLogAttributes.Success, "true"),
            (CodexLogAttributes.DurationMs, "125")
        );

        var result = adapter.Adapt(context);

        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.ToolResult);
        await Assert.That(result.Event.ToolName).IsEqualTo("mcp__zeeq__search_sections");
        await Assert.That(result.Event.Success).IsTrue();
        await Assert.That(result.Event.DurationMs).IsEqualTo(125);
    }

    [Test]
    public async Task Adapt_CompletedResponse_IdentifiesHousekeepingCompletion()
    {
        var adapter = new CodexTelemetryAdapter();
        var context = LogContext(
            (CodexLogAttributes.ConversationId, ConversationId),
            ("event.kind", "response.completed"),
            (CodexLogAttributes.InputTokenCount, "40"),
            (CodexLogAttributes.ToolTokenCount, "40"),
            (CodexLogAttributes.OutputTokenCount, "0"),
            (CodexLogAttributes.Model, "gpt-5")
        );

        var result = adapter.Adapt(context);

        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.Completion);
        await Assert.That(result.Event.IsHousekeeping).IsTrue();
        await Assert.That(result.Event.Model).IsEqualTo("gpt-5");
    }

    [Test]
    public async Task ShouldKeepLogRecord_UnrelatedCodexRecord_ReturnsFalse()
    {
        var adapter = new CodexTelemetryAdapter();
        var record = new OtlpLog.LogRecord();
        record.Attributes.Add(TestTelemetry.Attribute("message", "unrelated"));

        await Assert.That(adapter.ShouldKeepLogRecord(record, "codex")).IsFalse();
    }

    private static TelemetryLogRecordContext LogContext(
        params (string Key, string Value)[] attributes
    )
    {
        var record = new OtlpLog.LogRecord { Body = new AnyValue { StringValue = "codex" } };
        record.Attributes.Add(
            attributes.Select(attribute => TestTelemetry.Attribute(attribute.Key, attribute.Value))
        );

        return new TelemetryLogRecordContext(
            TestTelemetry.Resource(CodexLogAttributes.HarnessName),
            new InstrumentationScope { Name = "codex" },
            record
        );
    }
}
