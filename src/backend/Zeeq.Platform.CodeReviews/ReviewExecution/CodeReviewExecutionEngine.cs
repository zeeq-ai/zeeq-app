using System.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Executes the provider-neutral code-review workflow without owning persistence.
/// </summary>
/// <remarks>
/// The durable queue runner and draft-agent test flow both need identical PR snapshot
/// loading, repository filter application, prompt construction, library mapping, agent
/// execution, XML validation, and finding counts. This service owns that reusable core.
/// Callers decide whether the returned XML is persisted, returned directly, or discarded.
/// </remarks>
public sealed partial class CodeReviewExecutionEngine(
    ICodeReviewPullRequestSource pullRequestSource,
    ICodeRepositoryStore repositories,
    IPullRequestRecordStore pullRequests,
    CodeReviewerAgentResolver agentResolver,
    ICodeReviewAgentExecutor agentExecutor,
    ICodeReviewPreviousReviewStore previousReviewStore,
    CodeReviewXmlOutputValidator xmlValidator,
    ILibraryDocumentStore libraries,
    HybridCache cache,
    ILogger<CodeReviewExecutionEngine> logger
)
{
    /// <summary>
    /// Runs a code-review execution and returns validated XML plus parsed counts.
    /// </summary>
    /// <remarks>
    /// When <paramref name="runtimeAgentsOverride"/> is supplied, those runtime agents are
    /// treated as the configured reviewer set and filtered with the same activation rules
    /// as persisted agents. This is the draft-agent test path: disabled persisted state is
    /// irrelevant because the draft runtime agent is passed explicitly.
    /// </remarks>
    public async Task<CodeReviewExecutionResult> RunAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        CodeReviewExecutionOptions options,
        IReadOnlyList<CodeReviewerRuntimeAgent>? runtimeAgentsOverride,
        CancellationToken cancellationToken
    )
    {
        // Draft test runs intentionally share this engine but suppress code-controlled
        // Activity spans/events. The LLM/provider layer can still emit its own telemetry; this
        // flag only gates Zeeq review-execution diagnostics that would otherwise feed listeners.
        using var activity = options.EmitDiagnostics
            ? ZeeqTelemetry.Trace(
                [
                    ("organization.id", message.OrganizationId),
                    ("github.repo", message.OwnerQualifiedRepoName),
                    ("pull_request.number", message.PullRequestNumber),
                    ("code_review.id", review.Id),
                    ("code_review.execution_mode", options.Mode.ToString()),
                ],
                "code-review.runner.run"
            )
            : null;

        // Per-run collector for the KB sources each reviewer consults. Declared outside the
        // try so partial telemetry captured before a failure is still available to callers.
        var telemetry = new CodeReviewTelemetryContext(
            message.OrganizationId,
            message.RepositoryId
        );

        try
        {
            var repository = await LoadRepositoryAsync(message, cancellationToken);
            var pullRequest = await LoadPullRequestAsync(message, cancellationToken);

            // Always reload the live provider snapshot. Stored PR rows choose the target, but
            // the review must reflect the latest GitHub diff/body even for old closed PRs.
            var snapshot = await pullRequestSource.GetPullRequestAsync(message, cancellationToken);

            // Repository filters are applied before agent activation. The UI test flow can then
            // distinguish "the repository excluded every file" from "this draft agent did not
            // activate for any remaining file."
            var fileScope = CodeReviewFileFilterEvaluator.Apply(
                snapshot.Files,
                repository.ReviewConfiguration.FileFilter
            );

            // runtimeAgentsOverride is the draft-agent test hook. It bypasses persisted agent
            // enabled/disabled state but still uses the same activation predicate as saved agents.
            var agentResolution = await ResolveAgentsAsync(
                message,
                fileScope.InScopeFiles,
                runtimeAgentsOverride,
                cancellationToken
            );

            LogAgentsResolved(
                logger,
                review.Id,
                agentResolution.Agents.Count,
                agentResolution.HasConfiguredAgents,
                agentResolution.NoAgentsActivated
            );

            var executionContext = new CodeReviewExecutionContext(
                review,
                pullRequest,
                snapshot,
                repository.ReviewConfiguration,
                [.. agentResolution.Agents],
                fileScope.InScopeFiles,
                fileScope.OutOfScopeFiles
            );

            var mappedLibraryNames = await libraries.ResolveMappedLibraryNamesAsync(
                message.OrganizationId,
                repository.LibraryIds,
                cache,
                cancellationToken
            );

            LogLibrariesResolved(
                logger,
                message.OrganizationId,
                message.RepositoryId,
                mappedLibraryNames.Length
            );

            if (options.EmitDiagnostics)
            {
                activity?.AddEvent(
                    [
                        ("code_review.mapped_library_count", mappedLibraryNames.Length),
                        ("code_review.repository_id", message.RepositoryId),
                    ],
                    "code_review.libraries_resolved"
                );
            }

            var prompt = CodeReviewUserPrompt.From(
                executionContext.ToPromptInput(mappedLibraryNames)
            );

            LogPromptBuilt(logger, review.Id, prompt.SharedPullRequestPromptBody.Length);

            if (options.EmitDiagnostics)
            {
                activity?.AddEvent(
                    [
                        ("code_review.in_scope_file_count", fileScope.InScopeFiles.Count),
                        ("code_review.out_of_scope_file_count", fileScope.OutOfScopeFiles.Count),
                        ("code_review.reviewer_count", agentResolution.Agents.Count),
                        ("code_review.no_agents_activated", agentResolution.NoAgentsActivated),
                        (
                            "code_review.prompt_char_count",
                            prompt.SharedPullRequestPromptBody.Length
                        ),
                    ],
                    "code_review.runner_context_built"
                );
            }

            LogAgentExecutionStarting(
                logger,
                review.Id,
                agentResolution.Agents.Count,
                agentResolution.HasConfiguredAgents,
                agentResolution.NoAgentsActivated,
                mappedLibraryNames.Length,
                prompt.SharedPullRequestPromptBody.Length,
                fileScope.InScopeFiles.Count,
                fileScope.OutOfScopeFiles.Count
            );

            // Previous reviews are valuable for durable review chains, but a draft-agent test
            // should show what this single configured agent produces in isolation.
            var previousReviews =
                options.IncludePreviousReviews && !string.IsNullOrEmpty(review.ReviewGroupId)
                    ? await previousReviewStore.LoadAsync(
                        message.OrganizationId,
                        message.OwnerQualifiedRepoName,
                        message.PullRequestNumber,
                        review.ReviewGroupId,
                        review.Id,
                        cancellationToken: cancellationToken
                    )
                    : [];

            // Synthetic automation identity for the async webhook path and draft test path:
            // no real end-user principal exists once execution reaches this provider-neutral core.
            var callerIdentity = CodeReviewAutomationIdentity.Create(
                repository.OrganizationId,
                repository.TeamId
            );

            var xml = await agentExecutor.ExecuteAsync(
                message.OrganizationId,
                agentResolution.Agents,
                agentResolution.NoAgentsActivated,
                prompt,
                previousReviews,
                callerIdentity,
                telemetry,
                options,
                cancellationToken
            );

            // The engine returns both canonical XML and parsed output. Durable callers persist
            // the XML artifact; draft test callers can render the parsed output directly.
            var validation = ValidateXml(xml);
            var output =
                validation.Output
                ?? throw new InvalidOperationException("Validated code-review output was missing.");
            var counts = output.CountFindings();

            if (options.EmitDiagnostics)
            {
                activity?.AddEvent(
                    [
                        ("code_review.findings.critical", counts.Critical),
                        ("code_review.findings.major", counts.Major),
                        ("code_review.findings.minor", counts.Minor),
                        ("code_review.findings.suggestion", counts.Suggestion),
                        ("code_review.findings.comment", counts.Comment),
                    ],
                    "code_review.findings_validated"
                );
            }

            LogReviewXmlValidated(
                logger,
                review.Id,
                counts.Critical,
                counts.Major,
                counts.Minor,
                counts.Suggestion,
                counts.Comment
            );

            return new(
                Xml: xml,
                Output: output,
                SourceTelemetryPayload: telemetry.SerializeSnapshotPayload(),
                Counts: counts,
                InScopeFileCount: fileScope.InScopeFiles.Count,
                OutOfScopeFileCount: fileScope.OutOfScopeFiles.Count,
                ReviewerCount: agentResolution.Agents.Count,
                HasConfiguredAgents: agentResolution.HasConfiguredAgents,
                NoAgentsActivated: agentResolution.NoAgentsActivated
            );
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            if (options.EmitDiagnostics)
            {
                activity?.AddEvent(
                    [
                        ("exception.type", ex.GetType().Name),
                        ("exception.message", ex.Message),
                        ("code_review.id", review.Id),
                    ],
                    "code_review.runner_failed"
                );
            }

            LogRunnerFailed(logger, review.Id, ex.GetType().Name);

            // Durable callers persist this on the errored review row. Synthetic callers can
            // inspect the same partially captured source telemetry without writing a review.
            review.SourceTelemetryPayload = telemetry.SerializeSnapshotPayload();

            throw;
        }
    }

    private async Task<CodeReviewerAgentResolution> ResolveAgentsAsync(
        CodeReviewRunRequested message,
        IReadOnlyList<CodeReviewFileSnapshot> inScopeFiles,
        IReadOnlyList<CodeReviewerRuntimeAgent>? runtimeAgentsOverride,
        CancellationToken cancellationToken
    )
    {
        if (runtimeAgentsOverride is null)
        {
            return await agentResolver.ResolveAsync(
                message.OrganizationId,
                message.RepositoryId,
                inScopeFiles,
                cancellationToken
            );
        }

        var activeAgents = runtimeAgentsOverride
            .Where(agent =>
                CodeReviewerAgentResolver.IsActivated(agent.ActivationConfiguration, inScopeFiles)
            )
            .ToArray();

        return new(activeAgents, HasConfiguredAgents: true);
    }

    private async Task<CodeRepository> LoadRepositoryAsync(
        CodeReviewRunRequested message,
        CancellationToken cancellationToken
    ) =>
        await repositories.FindActiveForOrganizationAsync(
            message.OrganizationId,
            message.RepositoryId,
            cancellationToken
        )
        ?? throw new InvalidOperationException(
            $"Code review repository was not found. OrganizationId={message.OrganizationId}, RepositoryId={message.RepositoryId}"
        );

    private async Task<PullRequestRecord> LoadPullRequestAsync(
        CodeReviewRunRequested message,
        CancellationToken cancellationToken
    ) =>
        await pullRequests.FindAsync(
            message.PullRequestRecordId,
            message.PullRequestCreatedAtUtc,
            cancellationToken
        )
        ?? throw new InvalidOperationException(
            $"Pull request record was not found. Id={message.PullRequestRecordId}, CreatedAtUtc={message.PullRequestCreatedAtUtc:O}"
        );

    private CodeReviewXmlValidationResult ValidateXml(string xml)
    {
        var validation = xmlValidator.Validate(xml);
        if (!validation.IsValid || validation.Output is null)
        {
            throw new InvalidOperationException(
                $"Code-review runner produced invalid XML: {validation.ErrorMessage}"
            );
        }

        return validation;
    }

    [LoggerMessage(
        EventId = 3267,
        Level = LogLevel.Debug,
        Message = "Resolved code-review runtime agents. CodeReviewId={CodeReviewId}, ReviewerCount={ReviewerCount}, HasConfiguredAgents={HasConfiguredAgents}, NoAgentsActivated={NoAgentsActivated}"
    )]
    private static partial void LogAgentsResolved(
        ILogger logger,
        string codeReviewId,
        int reviewerCount,
        bool hasConfiguredAgents,
        bool noAgentsActivated
    );

    [LoggerMessage(
        EventId = 3268,
        Level = LogLevel.Debug,
        Message = "Built code-review prompt. CodeReviewId={CodeReviewId}, PromptLength={PromptLength}"
    )]
    private static partial void LogPromptBuilt(
        ILogger logger,
        string codeReviewId,
        int promptLength
    );

    [LoggerMessage(
        EventId = 3269,
        Level = LogLevel.Information,
        Message = "Starting code-review agent execution. CodeReviewId={CodeReviewId}, ReviewerCount={ReviewerCount}, HasConfiguredAgents={HasConfiguredAgents}, NoAgentsActivated={NoAgentsActivated}, MappedLibraryCount={MappedLibraryCount}, PromptLength={PromptLength}, InScopeFileCount={InScopeFileCount}, OutOfScopeFileCount={OutOfScopeFileCount}"
    )]
    private static partial void LogAgentExecutionStarting(
        ILogger logger,
        string codeReviewId,
        int reviewerCount,
        bool hasConfiguredAgents,
        bool noAgentsActivated,
        int mappedLibraryCount,
        int promptLength,
        int inScopeFileCount,
        int outOfScopeFileCount
    );

    [LoggerMessage(
        EventId = 3270,
        Level = LogLevel.Information,
        Message = "Validated code-review XML output. CodeReviewId={CodeReviewId}, Critical={CriticalFindings}, Major={MajorFindings}, Minor={MinorFindings}, Suggestion={SuggestionFindings}, Comment={CommentFindings}"
    )]
    private static partial void LogReviewXmlValidated(
        ILogger logger,
        string codeReviewId,
        int criticalFindings,
        int majorFindings,
        int minorFindings,
        int suggestionFindings,
        int commentFindings
    );

    [LoggerMessage(
        EventId = 3271,
        Level = LogLevel.Debug,
        Message = "Resolved mapped libraries for code review. OrganizationId={OrganizationId}, RepositoryId={RepositoryId}, MappedLibraryCount={MappedLibraryCount}"
    )]
    private static partial void LogLibrariesResolved(
        ILogger logger,
        string organizationId,
        string repositoryId,
        int mappedLibraryCount
    );

    [LoggerMessage(
        EventId = 3262,
        Level = LogLevel.Error,
        Message = "Code-review runner failed. CodeReviewId={CodeReviewId}, ErrorType={ErrorType}"
    )]
    private static partial void LogRunnerFailed(
        ILogger logger,
        string codeReviewId,
        string errorType
    );
}
