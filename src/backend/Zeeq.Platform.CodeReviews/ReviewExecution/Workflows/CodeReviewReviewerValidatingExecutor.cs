using System.Diagnostics;
using System.Diagnostics.Metrics;
using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Humanizer;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Workflow node that runs one reviewer agent and validates its XML block.
/// </summary>
/// <remarks>
/// Each persisted or fallback reviewer gets its own validating executor. The
/// executor asks the underlying <see cref="AIAgent" /> for a single
/// <c>&lt;review&gt;</c> block, validates the block by wrapping it in the
/// canonical <c>&lt;reviews&gt;</c> root, and gives the model up to two
/// self-correction attempts before emitting a failed-reviewer block.
///
/// A single malformed reviewer response should not fail the full code review.
/// The executor emits a schema-valid failure block for that reviewer so other
/// reviewer outputs can still be rendered and stored.
/// </remarks>
internal sealed partial class CodeReviewReviewerValidatingExecutor(
    string id,
    AIAgent reviewerAgent,
    CodeReviewerRuntimeAgent runtimeAgent,
    CodeReviewXmlOutputValidator xmlValidator,
    string previousReviewsSection,
    string provider,
    string model,
    ILoggerFactory loggerFactory,
    CodeReviewTelemetryContext? telemetry = null
) : Executor<ChatMessage, ChatMessage>(id)
{
    private const int MaxCorrectionAttempts = 2;

    private static readonly Histogram<double> ReviewDurationHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<double>(
            "zeeq_review_duration_ms",
            "Elapsed wall-clock time for one reviewer (initial run plus any corrections), by facet."
        );

    private static readonly Histogram<double> ReviewTokensHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<double>(
            "zeeq_review_tokens",
            "Provider-billed token total for one reviewer run, summed across every LLM round trip."
        );

    private readonly ILogger<CodeReviewReviewerValidatingExecutor> _logger =
        loggerFactory.CreateLogger<CodeReviewReviewerValidatingExecutor>();

    /// <summary>
    /// Creates a validating executor without node-local logging for use in tests.
    /// </summary>
    /// <remarks>
    /// Forwards <see cref="string.Empty"/> for <c>previousReviewsSection</c> so the 4-arg
    /// test ctor signature is unchanged and existing test call sites continue to compile.
    /// </remarks>
    public CodeReviewReviewerValidatingExecutor(
        string id,
        AIAgent reviewerAgent,
        CodeReviewerRuntimeAgent runtimeAgent,
        CodeReviewXmlOutputValidator xmlValidator
    )
        : this(
            id,
            reviewerAgent,
            runtimeAgent,
            xmlValidator,
            previousReviewsSection: string.Empty,
            provider: string.Empty,
            model: string.Empty,
            NullLoggerFactory.Instance
        ) { }

    /// <summary>
    /// Runs the reviewer and returns a validated reviewer XML block.
    /// </summary>
    public override async ValueTask<ChatMessage> HandleAsync(
        ChatMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    )
    {
        using var activity = ZeeqTelemetry.Trace(
            [
                ("code_review.reviewer.id", runtimeAgent.Id),
                ("code_review.reviewer.name", runtimeAgent.DisplayName),
                ("code_review.reviewer.facet", runtimeAgent.ReviewFacet),
            ],
            "code-review.reviewer.validate"
        );

        LogReviewerStarted(
            _logger,
            runtimeAgent.Id,
            runtimeAgent.ReviewFacet,
            runtimeAgent.ModelTier,
            provider,
            model
        );

        // Accumulates token usage across the reviewer's LLM round trips (initial + corrections)
        // via the factory's usage middleware; the elapsed timer covers the whole reviewer run.
        var usageSink = new LlmUsageSink();
        var reviewStartedAt = Stopwatch.GetTimestamp();

        try
        {
            // message.Text is SharedPullRequestPromptBody — the reviewer-neutral PR review body
            // broadcast identically to every reviewer by CodeReviewConcurrentStartExecutor.
            // Per-reviewer <identity> and <previous_reviews> are composed here, not upstream,
            // so the system prompt stays byte-stable across runs and the LLM prompt cache hits.
            var composedPrompt = runtimeAgent.ComposeUserPrompt(
                message.Text,
                previousReviewsSection
            );

            var originalReviewPrompt = composedPrompt;

            var outputText = await RunInitialReviewAsync(
                new ChatMessage(ChatRole.User, composedPrompt),
                usageSink,
                cancellationToken
            );

            for (var correctionAttempt = 0; ; correctionAttempt++)
            {
                var parsed = CodeReviewJsonOutputParser.TryParse(
                    outputText,
                    runtimeAgent,
                    out var facetOutput,
                    out var parseError
                );

                if (parsed && facetOutput is not null)
                {
                    activity?.AddEvent(
                        [
                            ("code_review.reviewer.id", runtimeAgent.Id),
                            ("code_review.reviewer.facet", runtimeAgent.ReviewFacet),
                            ("code_review.reviewer.correction_attempts", correctionAttempt),
                        ],
                        "code_review.reviewer_xml_validated"
                    );

                    LogReviewerValidated(
                        _logger,
                        runtimeAgent.Id,
                        runtimeAgent.ReviewFacet,
                        runtimeAgent.ModelTier,
                        correctionAttempt
                    );

                    // The reviewer now emits JSON (see CodeReviewOutputPrompt). Re-serialize the
                    // parsed model to the canonical XML <review> block so the workflow fan-in,
                    // aggregation, storage, and outbound rendering stay XML and remain unchanged.
                    return new ChatMessage(
                        ChatRole.Assistant,
                        xmlValidator.SerializeReviewerBlock(facetOutput)
                    )
                    {
                        AuthorName = runtimeAgent.ReviewFacet,
                    };
                }

                var validationError = parseError ?? "Reviewer output could not be validated.";

                activity?.AddEvent(
                    [
                        ("code_review.reviewer.id", runtimeAgent.Id),
                        ("code_review.reviewer.facet", runtimeAgent.ReviewFacet),
                        ("code_review.reviewer.correction_attempt", correctionAttempt),
                        ("exception.message", validationError),
                    ],
                    "code_review.reviewer_xml_invalid"
                );

                var diagnosticOutput = RuntimeConfig.IsDevelopment
                    ? outputText
                    : outputText.Truncate(200).Replace(Environment.NewLine, "");

                LogReviewerXmlInvalid(
                    _logger,
                    runtimeAgent.Id,
                    runtimeAgent.ReviewFacet,
                    correctionAttempt,
                    validationError,
                    diagnosticOutput
                );

                if (correctionAttempt >= MaxCorrectionAttempts)
                {
                    var failureMessage = CreateReviewerFailureMessage(validationError);

                    activity?.AddEvent(
                        [
                            ("code_review.reviewer.id", runtimeAgent.Id),
                            ("code_review.reviewer.facet", runtimeAgent.ReviewFacet),
                            ("code_review.reviewer.correction_attempts", correctionAttempt),
                            ("exception.message", failureMessage),
                        ],
                        "code_review.reviewer_placeholder_emitted"
                    );

                    return new ChatMessage(
                        ChatRole.Assistant,
                        xmlValidator.SerializeReviewerBlock(
                            xmlValidator.CreateFailedReviewerPlaceholder(
                                runtimeAgent,
                                failureMessage
                            )
                        )
                    )
                    {
                        AuthorName = runtimeAgent.ReviewFacet,
                    };
                }

                activity?.AddEvent(
                    [
                        ("code_review.reviewer.id", runtimeAgent.Id),
                        ("code_review.reviewer.facet", runtimeAgent.ReviewFacet),
                        ("code_review.reviewer.correction_attempt", correctionAttempt + 1),
                    ],
                    "code_review.reviewer_correction_started"
                );

                LogReviewerCorrectionStarted(
                    _logger,
                    runtimeAgent.Id,
                    runtimeAgent.ReviewFacet,
                    correctionAttempt + 1
                );

                outputText = await RunCorrectionAsync(
                    originalReviewPrompt,
                    outputText,
                    validationError,
                    correctionAttempt + 1,
                    usageSink,
                    cancellationToken
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.AddEvent(
                [
                    ("code_review.reviewer.id", runtimeAgent.Id),
                    ("code_review.reviewer.facet", runtimeAgent.ReviewFacet),
                    ("exception.type", ex.GetType().Name),
                    ("exception.message", ex.Message),
                ],
                "code_review.reviewer_failed"
            );

            var failureMessage = CreateReviewerFailureMessage(ex.Message);

            LogReviewerFailed(
                _logger,
                runtimeAgent.Id,
                runtimeAgent.ReviewFacet,
                ex.GetType().Name,
                failureMessage
            );

            return new ChatMessage(
                ChatRole.Assistant,
                xmlValidator.SerializeReviewerBlock(
                    xmlValidator.CreateFailedReviewerPlaceholder(runtimeAgent, failureMessage)
                )
            )
            {
                AuthorName = runtimeAgent.ReviewFacet,
            };
        }
        finally
        {
            RecordReviewMetrics(Stopwatch.GetElapsedTime(reviewStartedAt), usageSink);
        }
    }

    /// <summary>
    /// Records per-reviewer duration and token metrics for the metrics dashboard.
    /// </summary>
    /// <remarks>
    /// Emitted once per reviewer (all exit paths, via <c>finally</c>). Skipped when the run has no
    /// organization scope (the capture rule would drop it anyway). <c>zeeq_review_tokens</c> is
    /// emitted only when the provider actually reported usage — a provider that populates nothing
    /// yields no token row rather than a misleading zero (Phase 0 spike). The token total also
    /// rides along as a <c>tokens</c> tag on the duration histogram when known.
    /// </remarks>
    private void RecordReviewMetrics(TimeSpan duration, LlmUsageSink usageSink)
    {
        var organizationId = telemetry?.OrganizationId;
        if (string.IsNullOrEmpty(organizationId))
        {
            return;
        }

        List<(string Key, object? Value)> tags =
        [
            ("organization_id", (object)organizationId),
            ("facet", (object)runtimeAgent.ReviewFacet),
        ];

        var repositoryId = telemetry!.RepositoryId;
        if (!string.IsNullOrEmpty(repositoryId))
        {
            tags.Add(("repository_id", (object)repositoryId));
        }

        if (usageSink.HasUsage)
        {
            var tokens = usageSink.TotalTokens;
            List<(string Key, object? Value)> durationTags = [.. tags, ("tokens", (object)tokens)];
            ReviewDurationHistogram.Record(duration.TotalMilliseconds, [.. durationTags]);
            ReviewTokensHistogram.Record(tokens, [.. tags]);
        }
        else
        {
            ReviewDurationHistogram.Record(duration.TotalMilliseconds, [.. tags]);
        }
    }

    /// <summary>
    /// Accepts the start executor's turn token without invoking the reviewer twice.
    /// </summary>
    /// <remarks>
    /// The start executor preserves the V1 protocol by sending both prompt and
    /// turn token. This wrapper runs the <see cref="AIAgent" /> directly when it
    /// receives the prompt, so the token is intentionally a no-op here.
    /// </remarks>
    public ValueTask HandleAsync(
        TurnToken token,
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    ) => ValueTask.CompletedTask;

    /// <inheritdoc />
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder)
            .ConfigureRoutes(routes =>
            {
                routes.AddHandler<TurnToken>(HandleAsync);
            });

    private static string CreateReviewerFailureMessage(string message)
    {
        const int MaxLength = 500;

        var normalized = string.Join(
            " ",
            message
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
        );

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Reviewer output failed validation.";
        }

        return normalized.Length <= MaxLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, MaxLength), "...");
    }

    /// <summary>
    /// Builds the per-run options for a reviewer model call, attaching the telemetry run context
    /// when a collector is present.
    /// </summary>
    /// <remarks>
    /// The <see cref="CodeReviewTelemetryRunContext"/> is placed in
    /// <c>AdditionalProperties</c> so <see cref="CodeReviewTelemetryMiddleware"/> can read it back
    /// from <c>AIAgent.CurrentRunContext</c> during tool invocation and attribute sources to this
    /// reviewer's facet. When <c>telemetry</c> is null (tests, non-telemetry paths) the options
    /// carry only the token cap, so the middleware stays a no-op.
    /// </remarks>
    private ChatClientAgentRunOptions BuildRunOptions(LlmUsageSink usageSink)
    {
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = CodeReviewAgentExecutor.MaxReviewerOutputTokens,
            // Per-round-trip usage sink, read by LlmClientFactory's usage middleware. On the
            // ChatOptions (chat-level) so it reaches the chat client, unlike the agent-level
            // telemetry run context below.
            AdditionalProperties = new() { [LlmUsageSink.RunOptionsKey] = usageSink },
        };

        if (telemetry is null)
        {
            return new ChatClientAgentRunOptions(chatOptions);
        }

        return new ChatClientAgentRunOptions(chatOptions)
        {
            AdditionalProperties = new()
            {
                [CodeReviewTelemetryMiddleware.RunContextKey] = new CodeReviewTelemetryRunContext(
                    telemetry,
                    runtimeAgent.ReviewFacet
                ),
            },
        };
    }

    private async Task<string> RunInitialReviewAsync(
        ChatMessage message,
        LlmUsageSink usageSink,
        CancellationToken cancellationToken
    )
    {
        var startedAt = Stopwatch.GetTimestamp();

        var response = await reviewerAgent.RunAsync(
            message,
            options: BuildRunOptions(usageSink),
            cancellationToken: cancellationToken
        );

        LogReviewerModelInvocationCompleted(
            _logger,
            runtimeAgent.Id,
            runtimeAgent.ReviewFacet,
            runtimeAgent.ModelTier,
            provider,
            model,
            Stopwatch.GetElapsedTime(startedAt),
            response.Text.Length
        );

        return response.Text;
    }

    private async Task<string> RunCorrectionAsync(
        string originalReviewPrompt,
        string previousOutputText,
        string validationError,
        int correctionAttempt,
        LlmUsageSink usageSink,
        CancellationToken cancellationToken
    )
    {
        var startedAt = Stopwatch.GetTimestamp();

        var response = await reviewerAgent.RunAsync(
            options: BuildRunOptions(usageSink),
            message: $"""
            CRITICAL ERROR: Your previous response was not valid JSON in the required shape and could not be parsed.

            <json_parser_error>
            {validationError}
            </json_parser_error>

            Correction attempt: {correctionAttempt} of {MaxCorrectionAttempts}.

            Use the original review request as the source of truth. If the original request includes <file_patch> entries, do not state that no code diff was supplied. Preserve any substantive review content or findings from the previous response when possible. If it cannot be repaired safely, rerun the review from the original request.

            Output ONLY a single JSON object matching the required shape:
            - Top-level string fields "summary" and "details", both non-empty.
            - A "findings" array; use [] when there are none and explain in "details" that no actionable issues were found.
            - Each finding requires non-empty "level", "file", "summary", and "details". "level" is one of CRITICAL, MAJOR, MINOR, SUGGESTION, COMMENT. "line" (integer) and "side" ("LEFT"/"RIGHT") are optional.
            - Do NOT include "facet" or "agent" fields; they are assigned automatically.
            - Put all code snippets and Markdown inside the string fields. Do NOT wrap the JSON in code fences.

            <original_review_request>
            {originalReviewPrompt}
            </original_review_request>

            <previous_invalid_response>
            {previousOutputText}
            </previous_invalid_response>

            Output only the corrected JSON object and nothing else. Do not truncate your output.

            MAXIMUM OUTPUT TOKENS: {CodeReviewAgentExecutor.MaxReviewerOutputTokens}
            """,
            cancellationToken: cancellationToken
        );
        LogReviewerCorrectionModelInvocationCompleted(
            _logger,
            runtimeAgent.Id,
            runtimeAgent.ReviewFacet,
            runtimeAgent.ModelTier,
            provider,
            model,
            correctionAttempt,
            Stopwatch.GetElapsedTime(startedAt),
            response.Text.Length
        );

        return response.Text;
    }

    [LoggerMessage(
        EventId = 3250,
        Level = LogLevel.Information,
        Message = "👩🏻‍💻  Started code-review reviewer. ReviewerId={ReviewerId}, Facet={Facet}, ModelTier={ModelTier}, Provider={Provider}, Model={Model}"
    )]
    private static partial void LogReviewerStarted(
        ILogger logger,
        string reviewerId,
        string facet,
        CodeReviewModelTier modelTier,
        string provider,
        string model
    );

    [LoggerMessage(
        EventId = 3257,
        Level = LogLevel.Information,
        Message = "☑️  Received code-review reviewer model response. ReviewerId={ReviewerId}, Facet={Facet}, ModelTier={ModelTier}, Provider={Provider}, Model={Model}, Duration={Duration}, ResponseLength={ResponseLength}"
    )]
    private static partial void LogReviewerModelInvocationCompleted(
        ILogger logger,
        string reviewerId,
        string facet,
        CodeReviewModelTier modelTier,
        string provider,
        string model,
        TimeSpan duration,
        int responseLength
    );

    [LoggerMessage(
        EventId = 3251,
        Level = LogLevel.Information,
        Message = "✅  Validated code-review reviewer XML. ReviewerId={ReviewerId}, Facet={Facet}, ModelTier={ModelTier}, CorrectionAttempts={CorrectionAttempts}"
    )]
    private static partial void LogReviewerValidated(
        ILogger logger,
        string reviewerId,
        string facet,
        CodeReviewModelTier modelTier,
        int correctionAttempts
    );

    [LoggerMessage(
        EventId = 3252,
        Level = LogLevel.Warning,
        Message = "⚠️  Reviewer produced invalid XML. ReviewerId={ReviewerId}, Facet={Facet}, CorrectionAttempt={CorrectionAttempt}, ValidationError={ValidationError}, Fragment={Fragment}"
    )]
    private static partial void LogReviewerXmlInvalid(
        ILogger logger,
        string reviewerId,
        string facet,
        int correctionAttempt,
        string validationError,
        string fragment
    );

    [LoggerMessage(
        EventId = 3253,
        Level = LogLevel.Information,
        Message = "🧑🏻‍⚕️  Started reviewer XML correction. ReviewerId={ReviewerId}, Facet={Facet}, CorrectionAttempt={CorrectionAttempt}"
    )]
    private static partial void LogReviewerCorrectionStarted(
        ILogger logger,
        string reviewerId,
        string facet,
        int correctionAttempt
    );

    [LoggerMessage(
        EventId = 3259,
        Level = LogLevel.Information,
        Message = "Received code-review reviewer correction model response. ReviewerId={ReviewerId}, Facet={Facet}, ModelTier={ModelTier}, Provider={Provider}, Model={Model}, CorrectionAttempt={CorrectionAttempt}, Duration={Duration}, ResponseLength={ResponseLength}"
    )]
    private static partial void LogReviewerCorrectionModelInvocationCompleted(
        ILogger logger,
        string reviewerId,
        string facet,
        CodeReviewModelTier modelTier,
        string provider,
        string model,
        int correctionAttempt,
        TimeSpan duration,
        int responseLength
    );

    [LoggerMessage(
        EventId = 3255,
        Level = LogLevel.Error,
        Message = "❌  Reviewer execution failed. ReviewerId={ReviewerId}, Facet={Facet}, ErrorType={ErrorType}, ErrorMessage={ErrorMessage}"
    )]
    private static partial void LogReviewerFailed(
        ILogger logger,
        string reviewerId,
        string facet,
        string errorType,
        string errorMessage
    );
}
