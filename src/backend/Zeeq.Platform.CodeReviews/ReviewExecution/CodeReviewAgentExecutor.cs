using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Llm;
using Zeeq.Mcp.Documents;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runs active reviewer agents through the code-review Agent Framework workflow.
/// </summary>
/// <remarks>
/// This executor is the bridge between provider-neutral reviewer configuration
/// and workflow execution. It resolves each reviewer's semantic model tier into
/// a runtime chat client, wraps that client as an <see cref="AIAgent" /> with
/// reviewer-specific instructions, runs the fan-out/fan-in workflow, and
/// validates the final canonical <c>&lt;reviews&gt;</c> XML document.
///
/// Persistence remains outside this class. The runner slice that owns source
/// fetching, artifact writes, count updates, and terminal state transitions can
/// use this class as a pure agent-execution boundary.
/// </remarks>
public sealed partial class CodeReviewAgentExecutor(
    CodeReviewLlmTierResolver llmTierResolver,
    CodeReviewWorkflowFactory workflowFactory,
    CodeReviewXmlOutputValidator xmlValidator,
    ILoggerFactory loggerFactory,
    IServiceProvider services
) : ICodeReviewAgentExecutor
{
    private readonly ILogger<CodeReviewAgentExecutor> _logger =
        loggerFactory.CreateLogger<CodeReviewAgentExecutor>();

    /// <summary>
    /// Maximum number of tokens that a reviewer agent may produce in a single output.
    /// This is a hard limit to prevent runaway outputs and ensure that the workflow
    /// can complete within the LLM provider's token limits.
    /// </summary>
    public const int MaxReviewerOutputTokens = 16000;

    /// <summary>
    /// Runs all active reviewers and returns canonical review XML.
    /// </summary>
    public async Task<string> ExecuteAsync(
        string organizationId,
        IReadOnlyList<CodeReviewerRuntimeAgent> activeReviewers,
        bool noAgentsActivated,
        CodeReviewUserPrompt codeReviewUserPrompt,
        IReadOnlyList<CodeReviewPreviousReview> previousReviews,
        ClaimsPrincipal callerIdentity,
        CodeReviewTelemetryContext telemetry,
        CancellationToken cancellationToken
    )
    {
        using var activity = ZeeqTelemetry.Trace(
            [
                ("organization.id", organizationId),
                ("code_review.no_agents_activated", noAgentsActivated),
                ("code_review.configured_reviewer_count", activeReviewers.Count),
            ],
            "code-review.agent.execute"
        );

        if (noAgentsActivated)
        {
            activity?.AddEvent(
                [("organization.id", organizationId)],
                "code_review.no_agents_activated"
            );
            LogNoAgentsActivated(_logger, organizationId);

            return CodeReviewXmlOutputValidator.Serialize(
                new CodeReviewOutputDocument { NoAgentsActivated = true }
            );
        }

        if (activeReviewers.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "No active reviewers");
            LogNoActiveReviewers(_logger, organizationId);

            throw new InvalidOperationException(
                "At least one active code reviewer is required unless noAgentsActivated is true."
            );
        }

        var workflowReviewers = new List<CodeReviewWorkflowReviewer>(activeReviewers.Count);

        // Reviewer agents run concurrently in the workflow, so each gets its own DI
        // scope. Document tools resolve a scoped PostgresDbContext from the agent's
        // service provider; sharing a single scope across concurrent agents would
        // drive one DbContext from multiple threads and throw "A second operation
        // was started on this context instance". The scopes are disposed only after
        // the workflow completes because agents use their scoped services throughout
        // execution.
        //
        // This per-reviewer scope only isolates reviewers from each other. A single
        // reviewer can also fan its own tool calls out concurrently
        // (AllowConcurrentInvocation), so BuildLibraryTools wraps each tool in a
        // ScopedServiceAIFunction that opens a fresh child scope per invocation to
        // keep concurrent calls off a shared DbContext.
        var reviewerScopes = new List<AsyncServiceScope>(activeReviewers.Count);

        try
        {
            // Here, we convert each of the active reviewers into an Agent Framework
            // AIAgent that can be executed in the workflow. Each reviewer is resolved to a
            // chat client based on its configured model tier, and the agent instructions
            // are built to include the reviewer prompt and previous review context.
            foreach (var reviewer in activeReviewers)
            {
                var reviewerScope = services.CreateAsyncScope();
                reviewerScopes.Add(reviewerScope);
                var reviewerServices = reviewerScope.ServiceProvider;

                var resolvedClient = await llmTierResolver.ResolveChatClientAsync(
                    organizationId,
                    reviewer.ModelTier,
                    cancellationToken
                );

                var previousReviewsSection = BuildPreviousReviewsSection(previousReviews);

                workflowReviewers.Add(
                    new(
                        reviewer,
                        resolvedClient
                            .ChatClient.AsAIAgent(
                                instructions: BuildAgentSystemInstructions(reviewer),
                                name: reviewer.DisplayName,
                                description: $"Zeeq code-review agent for {reviewer.ReviewFacet}.",
                                tools: BuildLibraryTools(callerIdentity, reviewerServices),
                                loggerFactory: loggerFactory,
                                services: reviewerServices
                            )
                            .AsBuilder()
                            .UseOpenTelemetry(
                                sourceName: LlmTelemetry.ActivitySourceName,
                                configure: config =>
                                {
                                    config.EnableSensitiveData = true; // TODO: This should be only on local
                                }
                            )
                            // Captures which KB sources this reviewer consults; reads the per-run
                            // context back from AIAgent.CurrentRunContext during each tool call.
                            // A no-op unless the run options carry a telemetry run context. The
                            // 4-arg lambda selects the function-invocation middleware overload.
                            .Use(
                                (agent, context, next, token) =>
                                    CodeReviewTelemetryMiddleware.RecordToolInvocationAsync(
                                        agent,
                                        context,
                                        next,
                                        token
                                    )
                            )
                            .Build(reviewerServices),
                        resolvedClient.Provider,
                        resolvedClient.Model,
                        previousReviewsSection,
                        Telemetry: telemetry
                    )
                );
            }

            var reviewerFacets = JoinReviewerFacets(workflowReviewers);

            activity?.AddEvent(
                [
                    ("organization.id", organizationId),
                    ("code_review.reviewer_count", workflowReviewers.Count),
                    ("code_review.reviewer_facets", reviewerFacets),
                ],
                "code_review.reviewers_resolved"
            );

            LogReviewersResolved(_logger, organizationId, workflowReviewers.Count, reviewerFacets);

            return await ExecuteWorkflowAsync(
                workflowReviewers,
                codeReviewUserPrompt.SharedPullRequestPromptBody,
                cancellationToken
            );
        }
        finally
        {
            foreach (var reviewerScope in reviewerScopes)
            {
                await reviewerScope.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Runs already-created workflow reviewer agents and returns canonical review XML.
    /// </summary>
    /// <remarks>
    /// This internal overload keeps workflow behavior testable without requiring
    /// organization LLM settings or provider client construction in every test.
    /// </remarks>
    internal async Task<string> ExecuteWorkflowAsync(
        IReadOnlyList<CodeReviewWorkflowReviewer> reviewers,
        string sharedPullRequestPromptBody,
        CancellationToken cancellationToken
    )
    {
        var reviewerFacets = JoinReviewerFacets(reviewers);

        using var activity = ZeeqTelemetry.Trace(
            [
                ("code_review.reviewer_count", reviewers.Count),
                ("code_review.reviewer_facets", reviewerFacets),
            ],
            "code-review.workflow.execute"
        );

        LogWorkflowStarted(
            _logger,
            reviewers.Count,
            reviewerFacets,
            sharedPullRequestPromptBody.Length
        );

        try
        {
            var workflow = workflowFactory.Build(reviewers);

            var aggregateBlocks = await RunWorkflowAsync(
                workflow,
                sharedPullRequestPromptBody,
                _logger,
                cancellationToken
            );

            activity?.AddEvent(
                [
                    ("code_review.workflow_output_char_count", aggregateBlocks.Length),
                    ("code_review.reviewer_count", reviewers.Count),
                ],
                "code_review.workflow_output_received"
            );

            var xml = ValidateAndSerializeAggregateBlocks(aggregateBlocks);
            LogWorkflowCompleted(_logger, reviewers.Count, reviewerFacets, aggregateBlocks.Length);

            return xml;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);

            activity?.AddEvent(
                [
                    ("exception.type", ex.GetType().Name),
                    ("exception.message", ex.Message),
                    ("code_review.reviewer_count", reviewers.Count),
                    ("code_review.reviewer_facets", reviewerFacets),
                ],
                "code_review.workflow_failed"
            );
            LogWorkflowFailed(_logger, reviewers.Count, reviewerFacets, ex.GetType().Name);

            throw;
        }
    }

    /// <summary>
    /// Validates aggregated reviewer blocks and returns canonical review XML.
    /// </summary>
    /// <remarks>
    /// An empty aggregate means the workflow topology completed without any
    /// reviewer output, which is a workflow invariant failure rather than a
    /// model-output validation problem.
    /// </remarks>
    internal string ValidateAndSerializeAggregateBlocks(string aggregateBlocks)
    {
        if (string.IsNullOrWhiteSpace(aggregateBlocks))
        {
            throw new InvalidOperationException(
                "Code-review workflow completed without any reviewer outputs."
            );
        }

        var xml = $"""<reviews noAgentsActivated="false">{aggregateBlocks}</reviews>""";
        var validation = xmlValidator.Validate(xml);

        if (!validation.IsValid || validation.Output is null)
        {
            throw new InvalidOperationException(
                $"Aggregated code-review XML did not validate: {validation.ErrorMessage}"
            );
        }

        var findingCount = validation.Output.Reviews.Sum(review => review.Findings.Count);
        ZeeqTelemetry.AddEvent(
            [
                ("code_review.review_count", validation.Output.Reviews.Count),
                ("code_review.finding_count", findingCount),
            ],
            "code_review.aggregate_xml_validated"
        );

        return CodeReviewXmlOutputValidator.Serialize(validation.Output);
    }

    private static async Task<string> RunWorkflowAsync(
        Workflow workflow,
        string sharedPullRequestPromptBody,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var outputs = new List<string>();
        Exception? workflowException = null;
        string? failedExecutorId = null;

        LogWorkflowStreamingStarting(logger, sharedPullRequestPromptBody.Length);

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(
            workflow,
            sharedPullRequestPromptBody,
            sessionId: null,
            cancellationToken
        );

        LogWorkflowStreamingStarted(logger);

        await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
        {
            switch (workflowEvent)
            {
                case WorkflowOutputEvent outputEvent when outputEvent.Is<string>(out var output):
                    outputs.Add(output);
                    ZeeqTelemetry.AddEvent(
                        [
                            ("code_review.workflow_output_index", outputs.Count),
                            ("code_review.workflow_output_char_count", output.Length),
                        ],
                        "code_review.workflow_stream_output"
                    );
                    LogWorkflowStreamOutputReceived(logger, outputs.Count, output.Length);
                    break;
                case ExecutorFailedEvent executorFailedEvent:
                    workflowException = executorFailedEvent.Data;
                    failedExecutorId = executorFailedEvent.ExecutorId;
                    ZeeqTelemetry.AddEvent(
                        [
                            ("code_review.workflow_executor_id", executorFailedEvent.ExecutorId),
                            ("exception.type", executorFailedEvent.Data?.GetType().Name),
                            ("exception.message", executorFailedEvent.Data?.Message),
                        ],
                        "code_review.workflow_executor_failed"
                    );
                    LogWorkflowExecutorFailed(
                        logger,
                        executorFailedEvent.ExecutorId,
                        executorFailedEvent.Data?.GetType().Name,
                        executorFailedEvent.Data?.Message
                    );
                    break;
                case WorkflowErrorEvent workflowErrorEvent:
                    workflowException = workflowErrorEvent.Exception;
                    ZeeqTelemetry.AddEvent(
                        [
                            ("exception.type", workflowErrorEvent.Exception?.GetType().Name),
                            ("exception.message", workflowErrorEvent.Exception?.Message),
                        ],
                        "code_review.workflow_error"
                    );
                    LogWorkflowStreamFailed(
                        logger,
                        workflowErrorEvent.Exception?.GetType().Name,
                        workflowErrorEvent.Exception?.Message
                    );
                    break;
            }
        }
        LogWorkflowStreamingCompleted(logger, outputs.Count, workflowException is not null);

        return outputs.Count switch
        {
            1 => outputs[0],
            0 when workflowException is not null && failedExecutorId is not null =>
                throw new InvalidOperationException(
                    $"Code-review workflow failed in executor {failedExecutorId}.",
                    workflowException
                ),
            0 when workflowException is not null => throw new InvalidOperationException(
                "Code-review workflow failed before producing aggregate output.",
                workflowException
            ),
            0 => throw new InvalidOperationException(
                "Code-review workflow completed without producing aggregate output."
            ),
            _ => throw new InvalidOperationException(
                $"Code-review workflow produced {outputs.Count} aggregate outputs; expected exactly one."
            ),
        };
    }

    private static string JoinReviewerFacets(IReadOnlyList<CodeReviewWorkflowReviewer> reviewers) =>
        string.Join(",", reviewers.Select(reviewer => reviewer.RuntimeAgent.ReviewFacet));

    /// <summary>
    /// Builds the static agent system instructions for one reviewer.
    /// </summary>
    /// <remarks>
    /// The system prompt contains only reviewer-stable content: the reviewer's own prompt
    /// and the shared XML output instructions. Dynamic per-run content — the
    /// <c>&lt;identity&gt;</c> block and the <c>&lt;previous_reviews&gt;</c> section —
    /// is composed into the user prompt at execution time by
    /// <see cref="CodeReviewReviewerValidatingExecutor"/> via
    /// <see cref="CodeReviewerRuntimeAgent.ComposeUserPrompt"/>, so this prompt is byte-identical across
    /// runs for the same reviewer and can be reused from the LLM prompt cache.
    /// </remarks>
    /// <param name="reviewer">Reviewer whose prompt frames the system instructions.</param>
    /// <returns>The static agent system-prompt string.</returns>
    internal static string BuildAgentSystemInstructions(CodeReviewerRuntimeAgent reviewer) =>
        $"""
            {CodeReviewOutputPrompt.CommonInstructions}

            ---

            {reviewer.Prompt}
            """;

    /// <summary>
    /// Wraps <see cref="DocumentLibraryMcpTools"/> static methods as
    /// <see cref="AITool"/>s, hiding the <see cref="ClaimsPrincipal"/> and any
    /// DI-backed service parameters from the model-visible JSON schema and
    /// binding them server-side.
    /// </summary>
    /// <remarks>
    /// These methods are also MCP tools, where the MCP server resolves service
    /// parameters such as <see cref="ILibraryDocumentStore"/> from DI. When the
    /// review agent wraps them as <see cref="AIFunction"/> instances, that binding
    /// must be reproduced here: otherwise the AI SDK exposes the service parameter
    /// in the tool schema and tries to deserialize model-supplied JSON into the
    /// interface at call time, which throws
    /// <see cref="NotSupportedException"/> ("Deserialization of interface or
    /// abstract types is not supported"). The <see cref="IServiceProviderIsService"/>
    /// check mirrors the MCP server's own service-parameter detection.
    /// </remarks>
    /// <param name="callerIdentity">The fixed identity bound to every tool call.</param>
    /// <param name="services">
    /// The scoped provider used to detect service parameters and, at invocation
    /// time via <see cref="AIFunctionArguments.Services"/>, resolve them.
    /// </param>
    internal static IList<AITool> BuildLibraryTools(
        ClaimsPrincipal callerIdentity,
        IServiceProvider services
    )
    {
        var serviceInspector = services.GetService<IServiceProviderIsService>();

        var options = new AIFunctionFactoryOptions
        {
            ConfigureParameterBinding = parameter =>
            {
                // The caller identity is bound server-side and never exposed to the model.
                if (parameter.ParameterType == typeof(ClaimsPrincipal))
                {
                    return new()
                    {
                        ExcludeFromSchema = true,
                        BindParameter = (_, _) => callerIdentity,
                    };
                }

                // DI-backed parameters (for example ILibraryDocumentStore) are
                // resolved from the invocation service provider instead of being
                // deserialized from tool-call JSON.
                if (serviceInspector?.IsService(parameter.ParameterType) == true)
                {
                    return new() { ExcludeFromSchema = true, BindParameter = BindServiceParameter };
                }

                return default;
            },
        };

        // Each function is wrapped with WithScopedServices so a fresh DI scope is created per
        // invocation. Reviewer agents allow concurrent tool invocation, and the wrapped tools
        // resolve a scoped PostgresDbContext; a per-call scope keeps concurrent calls off a shared,
        // non-thread-safe DbContext. See ScopedServiceAIFunction.
        //
        // MarkCodeReviewExecutionScope stamps every per-invocation scope so the document stores
        // hide ExcludedFromCodeReviews documents from list/search on this path only — reviewers
        // never consult operational/informational documents. read_document_by_path is unaffected
        // by design: direct path resolution ignores the scope (see DocumentSearchScope).
        return
        [
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.ListLibraries, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.ListDocuments, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.ReadDocumentByPath, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.SearchDocuments, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.SearchCodeSnippets, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
            AIFunctionFactory
                .Create(DocumentLibraryMcpTools.SearchSections, options)
                .WithScopedServices(services, MarkCodeReviewExecutionScope),
        ];
    }

    /// <summary>
    /// Marks a per-invocation tool scope as code-review execution so document stores apply the
    /// <see cref="LibraryDocument.ExcludedFromCodeReviews"/> filter to list and search results.
    /// </summary>
    /// <remarks>
    /// Passed to <see cref="AIFunctionScopingExtensions.WithScopedServices"/> for every library
    /// tool in <see cref="BuildLibraryTools"/>. The scope object defaults to unmarked, so the
    /// interactive MCP server and HTTP endpoints (which never run this hook) stay unfiltered.
    /// </remarks>
    /// <param name="scopedServices">The fresh child scope created for one tool invocation.</param>
    internal static void MarkCodeReviewExecutionScope(IServiceProvider scopedServices) =>
        scopedServices.GetRequiredService<DocumentSearchScope>().ForCodeReviewExecution = true;

    /// <summary>
    /// Resolves a wrapped MCP tool service parameter from the invocation service provider.
    /// </summary>
    private static object BindServiceParameter(
        ParameterInfo parameter,
        AIFunctionArguments arguments
    )
    {
        var invocationServices =
            arguments.Services
            ?? throw new InvalidOperationException(
                $"Unable to resolve service parameter '{parameter.Name}' of type "
                    + $"'{parameter.ParameterType.FullName}' for a code-review tool invocation "
                    + "because no service provider was supplied to the agent."
            );

        return invocationServices.GetService(parameter.ParameterType)
            ?? throw new InvalidOperationException(
                $"Unable to resolve service parameter '{parameter.Name}' of type "
                    + $"'{parameter.ParameterType.FullName}' for a code-review tool invocation."
            );
    }

    internal static string BuildPreviousReviewsSection(
        IReadOnlyList<CodeReviewPreviousReview> previousReviews
    )
    {
        /*
          Example output:

          <previous_reviews>
            <review facet="Security">
              <summary>Previous security review</summary>
              <findings>
                <previous_finding level="CRITICAL" file="src/db.cs">
                  <summary>SQL injection risk</summary>
                </previous_finding>
                <previous_finding level="MINOR" file="src/utils.cs">
                  <summary>Missing null check</summary>
                </previous_finding>
              </findings>
            </review>
            <use_of_previous_reviews>
              - Previous reviews are context from prior reviewer facets.
              - Re-check CRITICAL and MAJOR findings against the current diff and re-raise only if they still exist.
              - Treat MINOR, SUGGESTION, and COMMENT findings as prior context; do not repeat them unless the current diff reintroduces or materially worsens the issue.
            </use_of_previous_reviews>
          </previous_reviews>
        */
        if (previousReviews.Count == 0)
        {
            return string.Empty;
        }

        var reviewsWithFindings = previousReviews
            .Where(review => review.Findings.Count > 0)
            .ToArray();

        if (reviewsWithFindings.Length == 0)
        {
            return string.Empty;
        }

        var doc = new XElement(
            "previous_reviews",
            reviewsWithFindings.Select(review => new XElement(
                "review",
                new XAttribute("facet", review.Facet),
                new XElement("summary", review.Summary),
                new XElement("findings", review.Findings.Select(BuildFindingElement))
            )),
            new XElement("use_of_previous_reviews", PreviousReviewsUsageGuidance)
        );

        return doc.ToString();
    }

    private static XElement BuildFindingElement(CodeReviewPreviousFinding finding)
    {
        var level = finding.Level.ToString().ToUpperInvariant();

        return new XElement(
            "previous_finding",
            new XAttribute("level", level),
            new XAttribute("file", finding.File),
            new XElement("summary", finding.Summary)
        // Just the summary; details add noise and volume to the token count.
        );
    }

    private const string PreviousReviewsUsageGuidance = """
        - The previous_reviews contain earlier rounds of findings from all reviewer facets
        - These are **NOT** current cycle findings!!  **ONLY FOR CONTEXT OF PREVIOUS REVIEW ROUNDS**
        - In the *current* pr_diff, pay attention to `NOTE` comments (e.g. // NOTE: ...,  -- NOTE: ..., /* NOTE: ...*/, idiomatic language comments) that address these previous findings; if present, *consider the finding resolved*
        - CRITICAL and MAJOR findings **without a nearby NOTE** are considered *unresolved*; re-check the pr_diff for these high risk findings and re-flag if they still exist without a NOTE or comment explaining it
        - For MINOR, SUGGESTION, and COMMENT findings, if the pr_diff does not materially worsen the issue or increase the risk, *consider the finding resolved*
        - **Do not repeat resolved findings in your current review** unless the pr_diff **materially increases risk, errors, or regressions without a NOTE** clearly explaining the reasoning
        """;

    [LoggerMessage(
        EventId = 3230,
        Level = LogLevel.Information,
        Message = "No code-review agents are activated. OrganizationId={OrganizationId}"
    )]
    private static partial void LogNoAgentsActivated(ILogger logger, string organizationId);

    [LoggerMessage(
        EventId = 3231,
        Level = LogLevel.Warning,
        Message = "No active code-review agents were available. OrganizationId={OrganizationId}"
    )]
    private static partial void LogNoActiveReviewers(ILogger logger, string organizationId);

    [LoggerMessage(
        EventId = 3232,
        Level = LogLevel.Debug,
        Message = "Resolved code-review agents. OrganizationId={OrganizationId}, ReviewerCount={ReviewerCount}, ReviewerFacets={ReviewerFacets}"
    )]
    private static partial void LogReviewersResolved(
        ILogger logger,
        string organizationId,
        int reviewerCount,
        string reviewerFacets
    );

    [LoggerMessage(
        EventId = 3233,
        Level = LogLevel.Information,
        Message = "Started code-review workflow. ReviewerCount={ReviewerCount}, ReviewerFacets={ReviewerFacets}, PromptLength={PromptLength}"
    )]
    private static partial void LogWorkflowStarted(
        ILogger logger,
        int reviewerCount,
        string reviewerFacets,
        int promptLength
    );

    [LoggerMessage(
        EventId = 3234,
        Level = LogLevel.Information,
        Message = "Completed code-review workflow. ReviewerCount={ReviewerCount}, ReviewerFacets={ReviewerFacets}, OutputLength={OutputLength}"
    )]
    private static partial void LogWorkflowCompleted(
        ILogger logger,
        int reviewerCount,
        string reviewerFacets,
        int outputLength
    );

    [LoggerMessage(
        EventId = 3235,
        Level = LogLevel.Error,
        Message = "Failed code-review workflow. ReviewerCount={ReviewerCount}, ReviewerFacets={ReviewerFacets}, ErrorType={ErrorType}"
    )]
    private static partial void LogWorkflowFailed(
        ILogger logger,
        int reviewerCount,
        string reviewerFacets,
        string errorType
    );

    [LoggerMessage(
        EventId = 3236,
        Level = LogLevel.Debug,
        Message = "Starting code-review workflow streaming run. PromptLength={PromptLength}"
    )]
    private static partial void LogWorkflowStreamingStarting(ILogger logger, int promptLength);

    [LoggerMessage(
        EventId = 3237,
        Level = LogLevel.Debug,
        Message = "Started code-review workflow streaming run."
    )]
    private static partial void LogWorkflowStreamingStarted(ILogger logger);

    [LoggerMessage(
        EventId = 3238,
        Level = LogLevel.Debug,
        Message = "Received code-review workflow stream output. OutputIndex={OutputIndex}, OutputLength={OutputLength}"
    )]
    private static partial void LogWorkflowStreamOutputReceived(
        ILogger logger,
        int outputIndex,
        int outputLength
    );

    [LoggerMessage(
        EventId = 3239,
        Level = LogLevel.Error,
        Message = "Code-review workflow executor failed. ExecutorId={ExecutorId}, ErrorType={ErrorType}, ErrorMessage={ErrorMessage}"
    )]
    private static partial void LogWorkflowExecutorFailed(
        ILogger logger,
        string executorId,
        string? errorType,
        string? errorMessage
    );

    [LoggerMessage(
        EventId = 3241,
        Level = LogLevel.Error,
        Message = "Code-review workflow stream failed. ErrorType={ErrorType}, ErrorMessage={ErrorMessage}"
    )]
    private static partial void LogWorkflowStreamFailed(
        ILogger logger,
        string? errorType,
        string? errorMessage
    );

    [LoggerMessage(
        EventId = 3242,
        Level = LogLevel.Debug,
        Message = "Completed code-review workflow streaming run. OutputCount={OutputCount}, HasWorkflowException={HasWorkflowException}"
    )]
    private static partial void LogWorkflowStreamingCompleted(
        ILogger logger,
        int outputCount,
        bool hasWorkflowException
    );
}
