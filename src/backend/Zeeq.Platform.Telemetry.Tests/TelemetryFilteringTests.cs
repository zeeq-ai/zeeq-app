using Zeeq.Platform.Telemetry.Adapters.ClaudeCode;
using Zeeq.Platform.Telemetry.Adapters.Codex;
using Zeeq.Platform.Telemetry.Adapters.Copilot;
using Zeeq.Platform.Telemetry.Filtering;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Tests the defensive filtering boundary before raw requests are persisted.
/// </summary>
[Category("Unit")]
public sealed class TelemetryFilteringTests
{
    [Test]
    public async Task PruneAcceptedLogsInPlace_ClaudeRecords_KeepsOnlySessionEvents()
    {
        var request = new ExportLogsServiceRequest();
        var resourceLogs = new ResourceLogs { Resource = TestTelemetry.Resource("claude-code") };
        var scopeLogs = new ScopeLogs();
        scopeLogs.LogRecords.Add(Log("claude_code.user_prompt"));
        scopeLogs.LogRecords.Add(Log("unrelated"));
        resourceLogs.ScopeLogs.Add(scopeLogs);
        request.ResourceLogs.Add(resourceLogs);

        var filter = new AgentTelemetryLogFilter([new ClaudeCodeTelemetryAdapter()]);

        var kept = filter.PruneAcceptedLogsInPlace(request);

        await Assert.That(kept).IsEqualTo(1);
        await Assert.That(scopeLogs.LogRecords).Count().IsEqualTo(1);
        await Assert
            .That(scopeLogs.LogRecords[0].Body.StringValue)
            .IsEqualTo("claude_code.user_prompt");
    }

    [Test]
    public async Task PruneAcceptedLogsInPlace_UnknownHarness_PreservesRecordForFutureAdapter()
    {
        var request = new ExportLogsServiceRequest();
        var resourceLogs = new ResourceLogs { Resource = TestTelemetry.Resource("future-agent") };
        var scopeLogs = new ScopeLogs();
        scopeLogs.LogRecords.Add(Log("anything"));
        resourceLogs.ScopeLogs.Add(scopeLogs);
        request.ResourceLogs.Add(resourceLogs);

        var filter = new AgentTelemetryLogFilter([new CodexTelemetryAdapter()]);

        await Assert.That(filter.PruneAcceptedLogsInPlace(request)).IsEqualTo(1);
        await Assert.That(scopeLogs.LogRecords).Count().IsEqualTo(1);
    }

    [Test]
    public async Task PruneAcceptedSpansInPlace_CopilotRecords_RequiresSessionId()
    {
        var request = new ExportTraceServiceRequest();
        var resourceSpans = new ResourceSpans { Resource = TestTelemetry.Resource("copilot-chat") };
        var scopeSpans = new ScopeSpans();
        scopeSpans.Spans.Add(
            TestTelemetry.Span(
                "aabbccddeeff00112233445566778899",
                "1111111111111111",
                "chat",
                strAttrs: [(CopilotChatSpanAttributes.ChatSessionId, "session-1")]
            )
        );
        scopeSpans.Spans.Add(
            TestTelemetry.Span("aabbccddeeff00112233445566778899", "2222222222222222", "chat")
        );
        resourceSpans.ScopeSpans.Add(scopeSpans);
        request.ResourceSpans.Add(resourceSpans);

        var filter = new AgentTelemetrySpanFilter([new CopilotChatTelemetryAdapter()]);

        await Assert.That(filter.PruneAcceptedSpansInPlace(request)).IsEqualTo(1);
        await Assert.That(scopeSpans.Spans).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ExtractLogsMetadata_UsesPrunedCountAndHarness()
    {
        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(new ResourceLogs { Resource = TestTelemetry.Resource("codex") });
        var extractor = new TelemetryRawLogMetadataExtractor();

        var metadata = extractor.ExtractLogsMetadata(
            request,
            ingestUserId: "user-1",
            prunedCount: 7
        );

        await Assert.That(metadata.Harness).IsEqualTo("codex");
        await Assert.That(metadata.RecordCount).IsEqualTo(7);
        await Assert.That(metadata.IngestUserId).IsEqualTo("user-1");
    }

    private static LogRecord Log(string body) => new() { Body = new() { StringValue = body } };
}
