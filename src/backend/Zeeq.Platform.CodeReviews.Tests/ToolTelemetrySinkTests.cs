using Zeeq.Core.Common;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests the ambient <see cref="ToolTelemetrySink" />: no-op without a scope, routing to the
/// active sink inside a scope, restore-on-dispose, and the best-effort swallow contract.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/ToolTelemetrySinkTests/*"
/// </summary>
public sealed class ToolTelemetrySinkTests
{
    private static ToolKnowledgeSource Source(string tool = "search_sections") =>
        new(tool, ToolKnowledgeSourceKind.Section, ToolKnowledgeSourceUsage.Searched);

    [Test]
    public async Task RecordSource_WhenNoScope_IsNoOpAndCurrentIsNull()
    {
        await Assert.That(ToolTelemetrySink.Current).IsNull();

        // Must not throw with no active scope (the real MCP server path).
        ToolTelemetrySink.RecordSource(Source());
        ToolTelemetrySink.RecordMissedQuery("search_sections", "no scope here");

        await Assert.That(ToolTelemetrySink.Current).IsNull();
    }

    [Test]
    public async Task BeginScope_RoutesRecordsToSink_AndRestoresOnDispose()
    {
        var sink = new RecordingSink();

        using (ToolTelemetrySink.BeginScope(sink))
        {
            await Assert.That(ToolTelemetrySink.Current).IsSameReferenceAs(sink);

            ToolTelemetrySink.RecordSource(Source("search_code_snippets"));
            ToolTelemetrySink.RecordMissedQuery("search_documents", "missing topic");
        }

        // Scope disposed → ambient sink cleared.
        await Assert.That(ToolTelemetrySink.Current).IsNull();

        await Assert.That(sink.Sources).HasSingleItem();
        await Assert.That(sink.Sources[0].ToolName).IsEqualTo("search_code_snippets");
        await Assert.That(sink.Misses).HasSingleItem();
        await Assert.That(sink.Misses[0]).IsEqualTo(("search_documents", "missing topic"));
    }

    [Test]
    public async Task BeginScope_NestedScopes_RestorePreviousSink()
    {
        var outer = new RecordingSink();
        var inner = new RecordingSink();

        using (ToolTelemetrySink.BeginScope(outer))
        {
            using (ToolTelemetrySink.BeginScope(inner))
            {
                await Assert.That(ToolTelemetrySink.Current).IsSameReferenceAs(inner);
            }

            await Assert.That(ToolTelemetrySink.Current).IsSameReferenceAs(outer);
        }

        await Assert.That(ToolTelemetrySink.Current).IsNull();
    }

    [Test]
    public async Task RecordSource_WhenSinkThrows_IsSwallowed()
    {
        using var _ = ToolTelemetrySink.BeginScope(new ThrowingSink());

        // Best-effort contract: a faulty sink must never surface into tool execution.
        ToolTelemetrySink.RecordSource(Source());
        ToolTelemetrySink.RecordMissedQuery("search_sections", "boom");

        await Assert.That(ToolTelemetrySink.Current).IsNotNull();
    }

    [Test]
    public async Task BeginScope_OutOfOrderDispose_DoesNotClobberNewerScope()
    {
        var outerSink = new RecordingSink();
        var innerSink = new RecordingSink();

        var outer = ToolTelemetrySink.BeginScope(outerSink);
        var inner = ToolTelemetrySink.BeginScope(innerSink);

        // Dispose the OUTER scope first (out-of-order). The identity guard must leave the newer
        // inner sink active rather than restoring the outer's prior value and misrouting telemetry.
        outer.Dispose();
        await Assert.That(ToolTelemetrySink.Current).IsSameReferenceAs(innerSink);

        // The inner sink is still the live collector, so records route to it.
        ToolTelemetrySink.RecordSource(Source());
        await Assert.That(innerSink.Sources).HasSingleItem();
        await Assert.That(outerSink.Sources).IsEmpty();

        inner.Dispose();
    }

    [Test]
    public async Task BeginScope_DoubleDispose_IsIdempotent()
    {
        var sink = new RecordingSink();

        var scope = ToolTelemetrySink.BeginScope(sink);
        scope.Dispose();
        scope.Dispose();

        await Assert.That(ToolTelemetrySink.Current).IsNull();
    }

    private sealed class RecordingSink : IToolTelemetrySink
    {
        public List<ToolKnowledgeSource> Sources { get; } = [];
        public List<(string Tool, string Query)> Misses { get; } = [];

        public void RecordSource(ToolKnowledgeSource source) => Sources.Add(source);

        public void RecordMissedQuery(string toolName, string query) =>
            Misses.Add((toolName, query));
    }

    private sealed class ThrowingSink : IToolTelemetrySink
    {
        public void RecordSource(ToolKnowledgeSource source) =>
            throw new InvalidOperationException("sink failure");

        public void RecordMissedQuery(string toolName, string query) =>
            throw new InvalidOperationException("sink failure");
    }
}
