using System.Diagnostics.CodeAnalysis;
using Zeeq.Core.Common;
using Microsoft.Agents.AI;
using FunctionInvocationContext = Microsoft.Extensions.AI.FunctionInvocationContext;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Per-run telemetry context threaded to reviewer agents via <c>RunOptions.AdditionalProperties</c>.
/// </summary>
/// <remarks>
/// <see cref="CodeReviewReviewerValidatingExecutor" /> attaches this to each
/// <c>ChatClientAgentRunOptions</c> before calling <c>agent.RunAsync</c>, and
/// <see cref="CodeReviewTelemetryMiddleware" /> reads it back from the framework's own
/// <c>AIAgent.CurrentRunContext</c> during tool invocation. The <see cref="Facet" /> is captured
/// per reviewer so concurrent reviewers attribute their sources correctly.
/// </remarks>
/// <param name="Telemetry">The run-scoped collector all reviewers share.</param>
/// <param name="Facet">The reviewer facet issuing the tool call, e.g. <c>Security</c>.</param>
internal sealed record CodeReviewTelemetryRunContext(
    CodeReviewTelemetryContext Telemetry,
    string Facet
);

/// <summary>
/// Agent Framework function-invocation middleware that captures code-review tool telemetry.
/// </summary>
/// <remarks>
/// Registered on each reviewer agent via <c>.AsBuilder().Use(RecordToolInvocationAsync)</c>. On
/// each tool call it reads the <see cref="CodeReviewTelemetryRunContext" /> from the framework's
/// own <c>AIAgent.CurrentRunContext.RunOptions.AdditionalProperties</c> — so the sink is live for
/// the exact span the tool runs, with no reliance on our <c>AsyncLocal</c> surviving the concurrent
/// workflow engine (proven by the Phase 0 spike). It opens the ambient
/// <see cref="ToolTelemetrySink" /> scope plus the facet scope for the duration of the call, then
/// records the invocation outcome. It is a pass-through no-op for non-review callers (no run
/// context present), and never changes function execution behavior.
/// </remarks>
internal static class CodeReviewTelemetryMiddleware
{
    /// <summary>Key under which the run context is stored in <c>RunOptions.AdditionalProperties</c>.</summary>
    public const string RunContextKey = "zeeq.code_review.telemetry";

    /// <summary>
    /// Sets the ambient sink + facet for this tool call, invokes the tool, and records the outcome.
    /// </summary>
    /// <param name="agent">The invoking agent (unused; the run context flows via the framework).</param>
    /// <param name="context">The function-invocation context for the tool being called.</param>
    /// <param name="next">The next delegate in the pipeline (the actual tool invocation).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The tool's result, unchanged.</returns>
    public static async ValueTask<object?> RecordToolInvocationAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetRunContext(out var run))
        {
            return await next(context, cancellationToken);
        }

        // The framework guarantees CurrentRunContext is set here, so the sink is live for exactly
        // the span the tool executes — no dependence on ExecutionContext flow through the engine.
        using var sinkScope = ToolTelemetrySink.BeginScope(run.Telemetry);
        using var facetScope = run.Telemetry.BeginToolInvocationScope(run.Facet);

        var succeeded = false;
        try
        {
            var result = await next(context, cancellationToken);
            succeeded = true;

            return result;
        }
        finally
        {
            run.Telemetry.RecordToolInvocation(context.Function.Name, succeeded);
        }
    }

    /// <summary>Reads the run context supplied for this agent run, if any.</summary>
    /// <remarks>
    /// NOTE: the coupling to <c>AIAgent.CurrentRunContext</c> and a string key in
    /// <c>AdditionalProperties</c> is intentional — this ambient run context is the Agent
    /// Framework's own per-run metadata surface (proven to reach the middleware under the
    /// concurrent workflow in the Phase 0 spike). The key is centralized in the single
    /// <see cref="RunContextKey" /> constant to avoid drift.
    /// </remarks>
    private static bool TryGetRunContext([NotNullWhen(true)] out CodeReviewTelemetryRunContext? run)
    {
        run = null;
        var properties = AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties;

        return properties is not null
            && properties.TryGetValue(RunContextKey, out var value)
            && (run = value as CodeReviewTelemetryRunContext) is not null;
    }
}
