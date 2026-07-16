using System.Diagnostics.Metrics;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Processing;

namespace Zeeq.Platform.Telemetry.Tests;

/// <summary>
/// Tests metric emission from normalized agent telemetry into the shared Zeeq meter.
/// </summary>
/// <remarks>
/// Run with:
/// <c>dotnet run --project src/backend/Zeeq.Platform.Telemetry.Tests --output detailed --disable-logo --treenode-filter "/*/*/AgentTelemetryMetricsTests/*"</c>.
///
/// These tests are marked <see cref="NotInParallelAttribute"/> because
/// <see cref="MeterListener"/> observes process-global meter state. Running multiple
/// metric-listener tests at the same time can cross-capture measurements emitted
/// by another test and turn a deterministic assertion into a scheduler-dependent one.
/// </remarks>
[Category("Unit")]
[NotInParallel]
public sealed class AgentTelemetryMetricsTests
{
    [Test]
    public async Task RecordNewSession_UsesOwnerEmailThenCreatedByFallback()
    {
        // Guards that session attribution prefers the harness-reported owner email and falls
        // back to the validated ingest principal when the harness cannot identify an owner.
        var measurements = Capture(() =>
        {
            AgentTelemetryMetrics.RecordNewSession(
                Conversation(ownerEmail: "agent@example.com", createdById: "user_123")
            );
            AgentTelemetryMetrics.RecordNewSession(
                Conversation(ownerEmail: null, createdById: "user_456")
            );
        });

        var sessions = measurements
            .Where(m => m.InstrumentName == AgentTelemetryMetrics.SessionCounterName)
            .ToArray();

        await Assert.That(sessions).Count().IsEqualTo(2);
        await Assert.That(sessions[0].Tags["organization_id"]).IsEqualTo("org_123");
        await Assert.That(sessions[0].Tags["user"]).IsEqualTo("agent@example.com");
        await Assert.That(sessions[0].Tags["harness"]).IsEqualTo("codex");
        await Assert.That(sessions[1].Tags["user"]).IsEqualTo("user_456");
    }

    [Test]
    public async Task RecordEvent_EmitsPromptToolTokenAndCostMetrics()
    {
        // Guards the public agent telemetry taxonomy: prompt/tool counters use promoted
        // user/tool tags, token usage is split by token_kind, and cost histograms keep USD
        // separate from provider-native raw billing units.
        var conversation = Conversation(ownerEmail: "agent@example.com", createdById: "user_123");
        var measurements = Capture(() =>
        {
            AgentTelemetryMetrics.RecordEvent(
                conversation,
                new AgentSessionEvent
                {
                    Id = "evt_prompt",
                    OrganizationId = "org_123",
                    ConversationId = "conversation_123",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    EventType = AgentSessionEventType.Prompt,
                }
            );
            AgentTelemetryMetrics.RecordEvent(
                conversation,
                new AgentSessionEvent
                {
                    Id = "evt_tool",
                    OrganizationId = "org_123",
                    ConversationId = "conversation_123",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    EventType = AgentSessionEventType.ToolResult,
                    ToolName = "mcp__zeeq__search_sections",
                    McpServer = "zeeq",
                    Success = true,
                }
            );
            AgentTelemetryMetrics.RecordEvent(
                conversation,
                new AgentSessionEvent
                {
                    Id = "evt_completion",
                    OrganizationId = "org_123",
                    ConversationId = "conversation_123",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    EventType = AgentSessionEventType.Completion,
                    Model = "gpt-5",
                    InputTokens = 100,
                    CachedTokens = 25,
                    OutputTokens = 40,
                    ReasoningTokens = 5,
                    ToolTokens = 3,
                    CostUsd = 0.0123m,
                    CostUnitsRaw = 987,
                    CostSource = AgentSessionEventCostSource.ReportedUsd,
                }
            );
        });

        var prompt = measurements.Single(m =>
            m.InstrumentName == AgentTelemetryMetrics.PromptCounterName
        );
        await Assert.That(prompt.Value).IsEqualTo(1);
        await Assert.That(prompt.Tags["user"]).IsEqualTo("agent@example.com");

        var tool = measurements.Single(m =>
            m.InstrumentName == AgentTelemetryMetrics.ToolCallCounterName
        );
        await Assert.That(tool.Value).IsEqualTo(1);
        await Assert.That(tool.Tags["tool_name"]).IsEqualTo("mcp__zeeq__search_sections");
        await Assert.That(tool.Tags["mcp_server"]).IsEqualTo("zeeq");
        await Assert.That(tool.Tags["success"]).IsEqualTo("True");

        var tokenMeasurements = measurements
            .Where(m => m.InstrumentName == AgentTelemetryMetrics.TokenUsageHistogramName)
            .ToArray();
        await Assert.That(tokenMeasurements).Count().IsEqualTo(5);
        await Assert
            .That(tokenMeasurements.Select(m => m.Tags["token_kind"]!))
            .IsEquivalentTo(["input", "cached", "output", "reasoning", "tool"]);
        await Assert.That(tokenMeasurements.All(m => m.Tags["model"] == "gpt-5")).IsTrue();

        var usd = measurements.Single(m =>
            m.InstrumentName == AgentTelemetryMetrics.CostUsdHistogramName
        );
        var rawUnits = measurements.Single(m =>
            m.InstrumentName == AgentTelemetryMetrics.CostUnitsRawHistogramName
        );

        await Assert.That(usd.Value).IsEqualTo(0.0123);
        await Assert.That(usd.Tags["cost_source"]).IsEqualTo("ReportedUsd");
        await Assert.That(rawUnits.Value).IsEqualTo(987);
        await Assert.That(rawUnits.Tags["cost_source"]).IsEqualTo("ReportedUsd");
    }

    [Test]
    public async Task RecordEvent_SkipsHousekeepingEvents()
    {
        // Guards that title generation, progress messages, and other housekeeping events do not
        // inflate user-facing prompt, tool, token, or cost metrics.
        var conversation = Conversation(ownerEmail: "agent@example.com", createdById: "user_123");
        var measurements = Capture(() =>
            AgentTelemetryMetrics.RecordEvent(
                conversation,
                new AgentSessionEvent
                {
                    Id = "evt_housekeeping",
                    OrganizationId = "org_123",
                    ConversationId = "conversation_123",
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    EventType = AgentSessionEventType.Prompt,
                    IsHousekeeping = true,
                }
            )
        );

        await Assert.That(measurements).IsEmpty();
    }

    private static AgentConversation Conversation(string? ownerEmail, string? createdById) =>
        new()
        {
            Id = "conversation_123",
            OrganizationId = "org_123",
            Harness = "codex",
            StartedAtUtc = DateTimeOffset.UtcNow,
            OwnerEmail = ownerEmail,
            CreatedById = createdById,
        };

    private static List<CapturedMeasurement> Capture(Action action)
    {
        var measurements = new List<CapturedMeasurement>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (
                ReferenceEquals(instrument.Meter, ZeeqTelemetry.Metrics)
                && instrument.Name.StartsWith("zeeq_agent_", StringComparison.Ordinal)
            )
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>(
            (instrument, value, tags, _) =>
                measurements.Add(CapturedMeasurement.From(instrument.Name, value, tags))
        );
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
                measurements.Add(CapturedMeasurement.From(instrument.Name, value, tags))
        );
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
                measurements.Add(CapturedMeasurement.From(instrument.Name, value, tags))
        );
        listener.Start();

        action();

        return measurements;
    }

    private sealed record CapturedMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, string?> Tags
    )
    {
        public static CapturedMeasurement From<T>(
            string instrumentName,
            T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags
        )
            where T : struct
        {
            var capturedTags = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var tag in tags)
            {
                capturedTags[tag.Key] = tag.Value?.ToString();
            }

            return new(instrumentName, Convert.ToDouble(value), capturedTags);
        }
    }
}
