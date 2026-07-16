using System.Runtime.CompilerServices;
using Zeeq.Core.Common;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests <see cref="CodeReviewTelemetryMiddleware" /> through a real agent run driven by a fake
/// chat client (no LLM): the middleware must open the sink + facet scope so a source recorded by a
/// tool is attributed to the reviewer facet and the invocation is counted — and must be a
/// pass-through no-op when no telemetry run context is present.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewTelemetryMiddlewareTests/*"
/// </summary>
public sealed class CodeReviewTelemetryMiddlewareTests
{
    [Test]
    public async Task Middleware_WithRunContext_AttributesSourceToFacetAndCountsInvocation()
    {
        var telemetry = new CodeReviewTelemetryContext();
        var agent = BuildAgent();

        var options = new ChatClientAgentRunOptions(new ChatOptions())
        {
            AdditionalProperties = new()
            {
                [CodeReviewTelemetryMiddleware.RunContextKey] = new CodeReviewTelemetryRunContext(
                    telemetry,
                    "Security"
                ),
            },
        };

        await agent.RunAsync("review this", options: options);

        var snapshot = telemetry.Snapshot();

        // The tool recorded one section source; the middleware attributed it to "Security".
        var document = snapshot.Documents.Single();
        await Assert.That(document.Path).IsEqualTo("/probe.md");
        await Assert.That(document.Facets).IsEquivalentTo(["Security"]);

        var snippet = document.Snippets.Single();
        await Assert.That(snippet.Facets).IsEquivalentTo(["Security"]);

        // The middleware also counted the tool invocation.
        var usage = snapshot.ToolUsage.Single();
        await Assert.That(usage.Tool).IsEqualTo("probe_search");
        await Assert.That(usage.Calls).IsEqualTo(1);
        await Assert.That(usage.Succeeded).IsEqualTo(1);
    }

    [Test]
    public async Task Middleware_WithoutRunContext_IsPassThroughAndRecordsNothing()
    {
        var telemetry = new CodeReviewTelemetryContext();
        var agent = BuildAgent();

        // No AdditionalProperties → no run context → middleware is a no-op. The tool still runs,
        // but its RecordSource lands nowhere (no ambient sink scope opened).
        await agent.RunAsync(
            "review this",
            options: new ChatClientAgentRunOptions(new ChatOptions())
        );

        await Assert.That(telemetry.Snapshot().IsEmpty).IsTrue();
        await Assert.That(ToolTelemetrySink.Current).IsNull();
    }

    /// <summary>
    /// Builds an agent whose tool records a section source, wrapped with the telemetry middleware —
    /// the exact production decoration from <c>CodeReviewAgentExecutor</c>.
    /// </summary>
    private static AIAgent BuildAgent()
    {
        var tool = AIFunctionFactory.Create(
            (string query) =>
            {
                ToolTelemetrySink.RecordSource(
                    new(
                        ToolName: "search_sections",
                        Kind: ToolKnowledgeSourceKind.Section,
                        Usage: ToolKnowledgeSourceUsage.Searched,
                        Library: "kb",
                        DocumentPath: "/probe.md",
                        DocumentTitle: "Probe",
                        Heading: "Probe > Section",
                        DocumentId: "doc_probe",
                        SnippetId: "sn_probe",
                        Rank: 1
                    )
                );

                return "ok";
            },
            "probe_search"
        );

        return new FakeToolCallingChatClient()
            .AsAIAgent(name: "probe", tools: [tool])
            .AsBuilder()
            .Use(
                (agent, context, next, token) =>
                    CodeReviewTelemetryMiddleware.RecordToolInvocationAsync(
                        agent,
                        context,
                        next,
                        token
                    )
            )
            .Build();
    }

    /// <summary>
    /// Minimal chat client that requests the probe tool once, then returns final text after the
    /// tool result, so the function-invoking pipeline (and the middleware) fires deterministically.
    /// </summary>
    private sealed class FakeToolCallingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var sawToolResult = messages.Any(message =>
                message.Contents.Any(content => content is FunctionResultContent)
            );

            if (sawToolResult)
            {
                return Task.FromResult(
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
                );
            }

            var call = new FunctionCallContent(
                callId: Guid.NewGuid().ToString("N"),
                name: "probe_search",
                arguments: new Dictionary<string, object?> { ["query"] = "logging" }
            );

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);

            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
