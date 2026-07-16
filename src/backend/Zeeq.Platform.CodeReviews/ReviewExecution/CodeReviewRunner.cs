using System.Diagnostics;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runs one queued code-review attempt end to end.
/// </summary>
/// <remarks>
/// This class owns the provider-neutral execution pipeline after
/// <see cref="CodeReviewRunRequestedHandler" /> has acquired the organization
/// execution lease. It loads current PR/repository context, applies repository
/// file filters, resolves reviewer agents, builds the deterministic prompt,
/// executes the Agent Framework workflow, validates the canonical XML, and
/// stores that XML as a findings artifact. It deliberately does not mutate the
/// review row; the handler remains the single terminal-state writer so status,
/// counts, artifact URI, budget decrement, locks, and comment signals move
/// together.
/// </remarks>
public sealed partial class CodeReviewRunner(
    ICodeReviewPullRequestSource pullRequestSource,
    ICodeRepositoryStore repositories,
    IPullRequestRecordStore pullRequests,
    CodeReviewerAgentResolver agentResolver,
    ICodeReviewAgentExecutor agentExecutor,
    ICodeReviewPreviousReviewStore previousReviewStore,
    CodeReviewXmlOutputValidator xmlValidator,
    ICodeReviewArtifactStore artifacts,
    ILibraryDocumentStore libraries,
    HybridCache cache,
    ILogger<CodeReviewRunner> logger
) : ICodeReviewRunner
{
    private const string FindingsContentType = "application/xml";

    /// <inheritdoc />
    public async Task<CodeReviewRunResult> RunAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        CancellationToken cancellationToken
    )
    {
        using var activity = ZeeqTelemetry.Trace(
            [
                ("organization.id", message.OrganizationId),
                ("github.repo", message.OwnerQualifiedRepoName),
                ("pull_request.number", message.PullRequestNumber),
                ("code_review.id", review.Id),
            ],
            "code-review.runner.run"
        );

        // Per-run collector for the KB sources each reviewer consults. Declared outside the try so
        // partial telemetry captured before a failure is still persisted on the errored branch.
        var telemetry = new CodeReviewTelemetryContext(
            message.OrganizationId,
            message.RepositoryId
        );

        try
        {
            var repository = await LoadRepositoryAsync(message, cancellationToken);
            var pullRequest = await LoadPullRequestAsync(message, cancellationToken);
            var snapshot = await pullRequestSource.GetPullRequestAsync(message, cancellationToken);

            var fileScope = CodeReviewFileFilterEvaluator.Apply(
                snapshot.Files,
                repository.ReviewConfiguration.FileFilter
            );

            var agentResolution = await agentResolver.ResolveAsync(
                message.OrganizationId,
                message.RepositoryId,
                fileScope.InScopeFiles,
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
                [.. agentResolution.Agents], // defensive copy before prompt captures it
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

            activity?.AddEvent(
                [
                    ("code_review.mapped_library_count", mappedLibraryNames.Length),
                    ("code_review.repository_id", message.RepositoryId),
                ],
                "code_review.libraries_resolved"
            );

            var prompt = CodeReviewUserPrompt.From(
                executionContext.ToPromptInput(mappedLibraryNames)
            );

            LogPromptBuilt(logger, review.Id, prompt.SharedPullRequestPromptBody.Length);

            activity?.AddEvent(
                [
                    ("code_review.in_scope_file_count", fileScope.InScopeFiles.Count),
                    ("code_review.out_of_scope_file_count", fileScope.OutOfScopeFiles.Count),
                    ("code_review.reviewer_count", agentResolution.Agents.Count),
                    ("code_review.no_agents_activated", agentResolution.NoAgentsActivated),
                    ("code_review.prompt_char_count", prompt.SharedPullRequestPromptBody.Length),
                ],
                "code_review.runner_context_built"
            );

            LogAgentExecutionStarting(
                logger,
                review.Id,
                agentResolution.Agents.Count,
                agentResolution.NoAgentsActivated
            );

            var previousReviews = !string.IsNullOrEmpty(review.ReviewGroupId)
                ? await previousReviewStore.LoadAsync(
                    message.OrganizationId,
                    message.OwnerQualifiedRepoName,
                    message.PullRequestNumber,
                    review.ReviewGroupId,
                    review.Id,
                    cancellationToken: cancellationToken
                )
                : [];

            // Synthetic automation identity for the async webhook path — no
            // real end-user principal exists in this queue-consumer context.
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
                cancellationToken
            );

            var validation = ValidateXml(xml);
            var counts = validation.Output!.CountFindings();

            LogReviewXmlValidated(
                logger,
                review.Id,
                counts.Critical,
                counts.Major,
                counts.Minor,
                counts.Suggestion,
                counts.Comment
            );

            var findingsStorageUri = await WriteFindingsAsync(review, xml, cancellationToken);

            activity?.AddEvent(
                [
                    ("code_review.findings_storage_uri", findingsStorageUri),
                    ("code_review.findings.critical", counts.Critical),
                    ("code_review.findings.major", counts.Major),
                    ("code_review.findings.minor", counts.Minor),
                    ("code_review.findings.suggestion", counts.Suggestion),
                    ("code_review.findings.comment", counts.Comment),
                ],
                "code_review.findings_artifact_written"
            );

            LogFindingsArtifactWritten(logger, review.Id, findingsStorageUri);

            return new(
                SourceTelemetryPayload: telemetry.SerializeSnapshotPayload(),
                FindingsStorageUri: findingsStorageUri,
                CriticalFindings: counts.Critical,
                MajorFindings: counts.Major,
                MinorFindings: counts.Minor,
                SuggestionFindings: counts.Suggestion,
                CommentFindings: counts.Comment
            );
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.AddEvent(
                [
                    ("exception.type", ex.GetType().Name),
                    ("exception.message", ex.Message),
                    ("code_review.id", review.Id),
                ],
                "code_review.runner_failed"
            );
            LogRunnerFailed(logger, review.Id, ex.GetType().Name);

            // Persist whatever sources were consulted before the failure — useful debugging signal.
            // The handler's errored branch normalizes this value.
            review.SourceTelemetryPayload = telemetry.SerializeSnapshotPayload();

            throw;
        }
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

    private async Task<string> WriteFindingsAsync(
        CodeReviewRecord review,
        string xml,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        return await artifacts.WriteFindingsAsync(
            review,
            stream,
            FindingsContentType,
            cancellationToken
        );
    }

    [LoggerMessage(
        EventId = 3267,
        Level = LogLevel.Information,
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
        Level = LogLevel.Information,
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
        Message = "Starting code-review agent execution. CodeReviewId={CodeReviewId}, ReviewerCount={ReviewerCount}, NoAgentsActivated={NoAgentsActivated}"
    )]
    private static partial void LogAgentExecutionStarting(
        ILogger logger,
        string codeReviewId,
        int reviewerCount,
        bool noAgentsActivated
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
        EventId = 3261,
        Level = LogLevel.Information,
        Message = "Wrote code-review findings artifact. CodeReviewId={CodeReviewId}, FindingsStorageUri={FindingsStorageUri}"
    )]
    private static partial void LogFindingsArtifactWritten(
        ILogger logger,
        string codeReviewId,
        string findingsStorageUri
    );

    [LoggerMessage(
        EventId = 3271,
        Level = LogLevel.Information,
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
        Message = "❌  Code-review runner failed. CodeReviewId={CodeReviewId}, ErrorType={ErrorType}"
    )]
    private static partial void LogRunnerFailed(
        ILogger logger,
        string codeReviewId,
        string errorType
    );
}
