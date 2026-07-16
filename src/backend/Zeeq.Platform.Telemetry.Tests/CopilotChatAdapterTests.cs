using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;
using Zeeq.Platform.Telemetry.Adapters.Copilot;
using OpenTelemetry.Proto.Resource.V1;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Copilot Chat adapter tests using span attribute patterns from scratch fixtures.
/// </summary>
[Category("Unit")]
public sealed class CopilotChatAdapterTests
{
    [Test]
    public async Task Adapt_SessionlessSpanWithRepositoryAndBranch_UsesBranchScopedConversationId()
    {
        var adapter = new CopilotChatTelemetryAdapter();
        var span = TestTelemetry.Span(
            TraceId,
            SpanId,
            "invoke_agent",
            strAttrs:
            [
                (
                    CopilotChatSpanAttributes.CopilotRepoRemoteUrl,
                    "https://github.com/owner/repo.git"
                ),
                (CopilotChatSpanAttributes.CopilotRepoHeadBranch, "feat/branch-first-linking"),
            ]
        );
        var context = new TelemetrySpanRecordContext(span, CopilotResource);

        await Assert.That(adapter.CanHandle(context)).IsTrue();
        await Assert.That(adapter.Adapt(context).Conversation.ConversationId).StartsWith("branch:");
    }

    private const string SessionId = "chat_session_abc123";
    private const string TraceId = "aabbccddeeff00112233445566778899";
    private const string SpanId = "1111111111111111";

    private static readonly Resource CopilotResource = TestTelemetry.Resource("copilot-chat");

    [Test]
    public async Task Adapt_InvokeAgent_CreatesPromptAndConversation()
    {
        var adapter = new CopilotChatTelemetryAdapter();
        var span = TestTelemetry.Span(
            TraceId,
            SpanId,
            "invoke_agent",
            strAttrs:
            [
                (CopilotChatSpanAttributes.ChatSessionId, SessionId),
                (CopilotChatSpanAttributes.CopilotUserRequest, "write a function"),
                (CopilotChatSpanAttributes.GenAiAgentName, "craft"),
                (CopilotChatSpanAttributes.GenAiRequestModel, "claude-sonnet-5"),
                (CopilotChatSpanAttributes.CopilotRepoRemoteUrl, "https://github.com/org/repo"),
                (CopilotChatSpanAttributes.CopilotRepoHeadBranch, "main"),
            ]
        );
        var ctx = new TelemetrySpanRecordContext(span, CopilotResource);

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.Prompt);
        await Assert.That(result.Event!.PromptText).IsEqualTo("write a function");
        await Assert.That(result.Event!.PromptLength).IsEqualTo(16);
        await Assert.That(result.Conversation.ConversationId).IsEqualTo(SessionId);
        await Assert
            .That(result.Conversation.RepoRemoteUrl)
            .IsEqualTo("https://github.com/org/repo");
    }

    [Test]
    public async Task Adapt_Chat_CreatesCompletionWithTokens()
    {
        var adapter = new CopilotChatTelemetryAdapter();
        var span = TestTelemetry.Span(
            TraceId,
            "2222222222222222",
            "chat",
            startNanos: 1,
            endNanos: 1_000_000_001,
            strAttrs:
            [
                ("gen_ai.operation.name", "chat"),
                (CopilotChatSpanAttributes.ChatSessionId, SessionId),
                (CopilotChatSpanAttributes.GenAiResponseModel, "claude-sonnet-5"),
            ],
            intAttrs:
            [
                (CopilotChatSpanAttributes.GenAiUsageInputTokens, 5000),
                (CopilotChatSpanAttributes.GenAiUsageOutputTokens, 800),
                (CopilotChatSpanAttributes.CopilotUsageNanoAiu, 3_500_000),
            ]
        );

        var ctx = new TelemetrySpanRecordContext(span, CopilotResource);

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.Completion);
        await Assert.That(result.Event!.InputTokens).IsEqualTo(5000);
        await Assert.That(result.Event!.OutputTokens).IsEqualTo(800);
        await Assert.That(result.Event!.Model).IsEqualTo("claude-sonnet-5");
        await Assert.That(result.Event!.CostUnitsRaw).IsEqualTo(3_500_000);
    }

    [Test]
    public async Task Adapt_ProgressMessages_IsHousekeeping()
    {
        var adapter = new CopilotChatTelemetryAdapter();
        var span = TestTelemetry.Span(
            TraceId,
            SpanId,
            "chat",
            strAttrs:
            [
                (CopilotChatSpanAttributes.ChatSessionId, SessionId),
                (CopilotChatSpanAttributes.GenAiAgentName, "progressMessages"),
            ]
        );
        var ctx = new TelemetrySpanRecordContext(span, CopilotResource);

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.IsHousekeeping).IsTrue();
    }

    [Test]
    public async Task Adapt_ExecuteToolExtension_CreatesToolResult()
    {
        var adapter = new CopilotChatTelemetryAdapter();
        var span = TestTelemetry.Span(
            TraceId,
            "3333333333333333",
            "execute_tool",
            startNanos: 1,
            endNanos: 500_000_001,
            strAttrs:
            [
                ("gen_ai.operation.name", "execute_tool"),
                (CopilotChatSpanAttributes.ChatSessionId, SessionId),
                (CopilotChatSpanAttributes.GenAiToolType, "extension"),
                (CopilotChatSpanAttributes.GenAiToolName, "get_resources"),
                (CopilotChatSpanAttributes.McpServerName, "aspire"),
                (CopilotChatSpanAttributes.GenAiToolCallArguments, "{}"),
                (CopilotChatSpanAttributes.GenAiToolCallResult, "done"),
            ],
            intAttrs: []
        );

        var ctx = new TelemetrySpanRecordContext(span, CopilotResource);

        await Assert.That(adapter.CanHandle(ctx)).IsTrue();

        var result = adapter.Adapt(ctx);
        await Assert.That(result.Event).IsNotNull();
        await Assert.That(result.Event!.EventType).IsEqualTo(AgentSessionEventType.ToolResult);
        await Assert.That(result.Event!.ToolName).IsEqualTo("mcp__aspire__get_resources");
        await Assert.That(result.Event!.DurationMs).IsEqualTo(500);
    }

    [Test]
    public async Task Adapt_NoSessionId_NotHandled()
    {
        var adapter = new CopilotChatTelemetryAdapter();
        var span = TestTelemetry.Span(TraceId, SpanId, "chat");
        var ctx = new TelemetrySpanRecordContext(span, CopilotResource);

        await Assert.That(adapter.CanHandle(ctx)).IsFalse();
    }
}
