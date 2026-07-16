using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Builds the Agent Framework fan-out/fan-in workflow for one code review run.
/// </summary>
/// <remarks>
/// The graph is intentionally rebuilt per review run because reviewer agents,
/// prompts, and model-tier clients are runtime state. The topology is stable:
/// one start node broadcasts to one validating node per reviewer, then a fan-in
/// barrier delivers all validated reviewer blocks to the aggregation node.
/// </remarks>
public sealed class CodeReviewWorkflowFactory(
    CodeReviewXmlOutputValidator xmlValidator,
    ILoggerFactory loggerFactory
)
{
    /// <summary>
    /// Creates a workflow factory without node-local logging.
    /// </summary>
    /// <remarks>
    /// Tests can continue constructing the factory with only the XML validator.
    /// Production registration supplies the application logger factory so each
    /// executor emits source-generated logs in the same trace as the workflow.
    /// </remarks>
    public CodeReviewWorkflowFactory(CodeReviewXmlOutputValidator xmlValidator)
        : this(xmlValidator, NullLoggerFactory.Instance) { }

    /// <summary>
    /// Builds a workflow for the supplied reviewer agents.
    /// </summary>
    public Workflow Build(IReadOnlyList<CodeReviewWorkflowReviewer> reviewers)
    {
        if (reviewers.Count == 0)
        {
            throw new ArgumentException("At least one reviewer is required.", nameof(reviewers));
        }

        var startBinding = BindFactory(
            "CodeReviewConcurrentStartExecutor",
            _ => new CodeReviewConcurrentStartExecutor()
        );
        var aggregationBinding = BindFactory(
            "CodeReviewConcurrentAggregationExecutor",
            _ => new CodeReviewConcurrentAggregationExecutor(loggerFactory)
        );
        ExecutorBinding[] reviewerBindings =
        [
            .. reviewers.Select(
                (reviewer, index) =>
                    BindFactory(
                        $"CodeReviewReviewerValidatingExecutor_{index}",
                        id => new CodeReviewReviewerValidatingExecutor(
                            id,
                            reviewer.Agent,
                            reviewer.RuntimeAgent,
                            xmlValidator,
                            reviewer.PreviousReviewsSection,
                            reviewer.Provider,
                            reviewer.Model,
                            loggerFactory,
                            reviewer.Telemetry
                        )
                    )
            ),
        ];

        return new WorkflowBuilder(startBinding)
            .WithName("Zeeq Code Review")
            .WithDescription("Runs reviewer agents concurrently and aggregates validated XML.")
            .AddFanOutEdge(startBinding, reviewerBindings, "broadcast_prompt")
            .AddFanInBarrierEdge(reviewerBindings, aggregationBinding, "aggregate_reviewers")
            .WithOutputFrom(aggregationBinding)
            .Build(validateOrphans: true);
    }

    private static ExecutorBinding BindFactory<TExecutor>(
        string id,
        Func<string, TExecutor> createExecutor
    )
        where TExecutor : Executor =>
        ExecutorBindingExtensions.BindExecutor<TExecutor>(
            (executorId, _) => ValueTask.FromResult(createExecutor(executorId)),
            id
        );
}

/// <summary>
/// Runtime reviewer agent bound to an Agent Framework agent instance.
/// </summary>
/// <param name="RuntimeAgent">Provider-neutral reviewer settings resolved for this run.</param>
/// <param name="Agent">Agent Framework agent that should produce the reviewer XML block.</param>
/// <param name="Provider">Resolved LLM provider name (e.g. <c>Fireworks</c>) for diagnostic logging.</param>
/// <param name="Model">Resolved LLM model identifier for diagnostic logging.</param>
/// <param name="PreviousReviewsSection">
/// Pre-rendered <c>&lt;previous_reviews&gt;</c> XML for this reviewer's facet, or empty when none.
/// Injected into <see cref="CodeReviewReviewerValidatingExecutor"/> so it can compose the final
/// per-reviewer user prompt at execution time.
/// </param>
/// <param name="Telemetry">
/// Optional run-scoped telemetry collector. When set, the validating executor threads a
/// <see cref="CodeReviewTelemetryRunContext"/> through this reviewer's run options so the telemetry
/// middleware can attribute the sources it consults; null in tests that do not exercise telemetry.
/// </param>
public sealed record CodeReviewWorkflowReviewer(
    CodeReviewerRuntimeAgent RuntimeAgent,
    AIAgent Agent,
    string Provider,
    string Model,
    string PreviousReviewsSection = "",
    CodeReviewTelemetryContext? Telemetry = null
);
