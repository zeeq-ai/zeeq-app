using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;
using Zeeq.Platform.Telemetry.Adapters.ClaudeCode;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OtlpLog = OpenTelemetry.Proto.Logs.V1;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Adapter tests using attribute data from scratch fixture captures.
/// </summary>
[Category("Unit")]
public sealed class ClaudeCodeAdapterTests
{
    private static readonly string SessionId = "3645efe2-1d5e-46e5-805d-0f3ece932a35";

    private static TelemetryLogRecordContext LogCtx(
        string body,
        string? serviceName = null,
        params (string Key, string Value)[] attrs
    )
    {
        var logRecord = new OtlpLog.LogRecord
        {
            Body = new AnyValue { StringValue = body },
            TimeUnixNano = 0,
        };
        foreach (var (key, value) in attrs)
        {
            logRecord.Attributes.Add(TestTelemetry.Attribute(key, value));
        }

        var resource = new Resource();
        resource.Attributes.Add(
            TestTelemetry.Attribute("service.name", serviceName ?? "claude-code")
        );

        var scope = new InstrumentationScope { Name = "com.anthropic.claude_code.events" };

        return new TelemetryLogRecordContext(resource, scope, logRecord);
    }

    [Test]
    public async Task Adapt_UserPrompt_CreatesPromptEvent()
    {
        var adapter = new ClaudeCodeTelemetryAdapter();
        var ctx = LogCtx(
            "claude_code.user_prompt",
            attrs:
            [
                ("session.id", SessionId),
                ("event.sequence", "1"),
                ("event.timestamp", "2026-07-13T22:52:15.000Z"),
            ]
        );

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.Prompt);
        await Assert.That(result.Conversation.ConversationId).IsEqualTo(SessionId);
    }

    [Test]
    public async Task Adapt_Completion_ExtractsCostAndTokens()
    {
        var adapter = new ClaudeCodeTelemetryAdapter();
        var ctx = LogCtx(
            "claude_code.api_request",
            attrs:
            [
                ("session.id", SessionId),
                ("event.sequence", "2"),
                ("event.timestamp", "2026-07-13T22:52:16.000Z"),
                ("input_tokens", "1000"),
                ("output_tokens", "500"),
                ("cache_read_tokens", "200"),
                ("cache_creation_tokens", "100"),
                ("cost_usd", "0.015"),
                ("model", "claude-sonnet-5"),
                ("prompt.id", "prompt_abc"),
                ("request.id", "req_xyz"),
            ]
        );

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.Completion);
        await Assert.That(result.Event!.InputTokens).IsEqualTo(1300); // 1000 + 200 + 100
        await Assert.That(result.Event!.OutputTokens).IsEqualTo(500);
        await Assert.That(result.Event!.CostUsd).IsEqualTo(0.015m);
        await Assert
            .That(result.Event!.CostSource)
            .IsEqualTo(AgentSessionEventCostSource.ReportedUsd);
        await Assert.That(result.Event!.Model).IsEqualTo("claude-sonnet-5");
    }

    [Test]
    public async Task Adapt_Completion_UsesCostMicrosWhenUsdMissing()
    {
        var adapter = new ClaudeCodeTelemetryAdapter();
        var ctx = LogCtx(
            "claude_code.api_request",
            attrs:
            [
                ("session.id", SessionId),
                ("event.sequence", "3"),
                ("event.timestamp", "2026-07-13T22:52:17.000Z"),
                ("cost_usd_micros", "15000"), // 0.015 USD
            ]
        );

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event!.CostUsd).IsEqualTo(0.015m);
    }

    [Test]
    public async Task Adapt_ToolResult_CreatesToolResultEvent()
    {
        var adapter = new ClaudeCodeTelemetryAdapter();
        var ctx = LogCtx(
            "claude_code.tool_result",
            attrs:
            [
                ("session.id", SessionId),
                ("event.sequence", "4"),
                ("tool_name", "mcp_tool"),
                ("tool_parameters", ""),
                ("success", "true"),
                ("duration_ms", "150"),
                ("tool_use_id", "toolu_abc"),
            ]
        );

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.ToolResult);
        await Assert.That(result.Event!.Success).IsTrue();
        await Assert.That(result.Event!.DurationMs).IsEqualTo(150);
    }

    [Test]
    public async Task Adapt_ToolDecision_CreatesDecisionEvent()
    {
        var adapter = new ClaudeCodeTelemetryAdapter();
        var ctx = LogCtx(
            "claude_code.tool_decision",
            attrs:
            [
                ("session.id", SessionId),
                ("event.sequence", "5"),
                ("decision", "accept"),
                ("source", "auto"),
                ("tool_use_id", "toolu_abc"),
            ]
        );

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.ToolDecision);
        await Assert.That(result.Event!.Decision).IsEqualTo("accept");
    }

    [Test]
    public async Task Adapt_GenerateSessionTitle_IsHousekeeping()
    {
        var adapter = new ClaudeCodeTelemetryAdapter();
        var ctx = LogCtx(
            "claude_code.api_request",
            attrs:
            [
                ("session.id", SessionId),
                ("event.sequence", "6"),
                ("query_source", "generate_session_title"),
            ]
        );

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.IsHousekeeping).IsTrue();
    }
}
