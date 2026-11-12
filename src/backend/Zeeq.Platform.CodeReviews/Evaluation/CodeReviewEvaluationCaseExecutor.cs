using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>One scoped evaluation case pipeline.</summary>
public interface ICodeReviewEvaluationCaseExecutor
{
    /// <summary>Runs one frozen case under the owning lease.</summary>
    Task ExecuteAsync(
        CodeReviewEvaluationRun run,
        CodeReviewEvaluationRunManifest manifest,
        CodeReviewEvaluationManifestCase item,
        string leaseToken,
        CancellationToken cancellationToken
    );
}

/// <summary>Runs and persists one evaluation case under the run lease.</summary>
public sealed partial class CodeReviewEvaluationCaseExecutor(
    ICodeReviewEvaluationCaseResultStore results,
    ICodeReviewEvaluationArtifactStore artifacts,
    CodeReviewExecutionEngine engine,
    CodeReviewXmlOutputValidator validator,
    ICodeReviewEvaluationJudge judge,
    ILogger<CodeReviewEvaluationCaseExecutor> logger
) : ICodeReviewEvaluationCaseExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Executes one case or reuses a failed parent's candidate for judge-only retry.</summary>
    public async Task ExecuteAsync(
        CodeReviewEvaluationRun run,
        CodeReviewEvaluationRunManifest manifest,
        CodeReviewEvaluationManifestCase item,
        string leaseToken,
        CancellationToken cancellationToken
    )
    {
        var org = run.OrganizationId;
        var stage = CodeReviewEvaluationCaseOutcome.ReviewFailed;
        var operation = "LoadParent";
        try
        {
            CodeReviewEvaluationArtifact? reused = null;
            if (run.ParentRunId is { } parentId)
            {
                // Admission copies candidate metadata only from this immutable parent row.
                // A complete inherited candidate therefore has the same complete parent identity.
                var parent = await results.FindAsync(org, parentId, item.CaseId, cancellationToken);
                if (
                    parent is null
                    || parent.Outcome
                        is not (
                            CodeReviewEvaluationCaseOutcome.ReviewFailed
                            or CodeReviewEvaluationCaseOutcome.JudgeFailed
                            or CodeReviewEvaluationCaseOutcome.Cancelled
                            or CodeReviewEvaluationCaseOutcome.None
                        )
                )
                    throw new InvalidOperationException("Parent case is not eligible for retry.");
                if (
                    parent is
                    {
                        Outcome: CodeReviewEvaluationCaseOutcome.JudgeFailed
                            or CodeReviewEvaluationCaseOutcome.Cancelled
                            or CodeReviewEvaluationCaseOutcome.None,
                        CandidateStorageUri: not null,
                        CandidateSha256: not null,
                        CandidateByteCount: not null
                    }
                )
                    reused = new(
                        parent.CandidateStorageUri,
                        parent.CandidateSha256,
                        parent.CandidateByteCount.Value
                    );
                else if (
                    parent.Outcome == CodeReviewEvaluationCaseOutcome.JudgeFailed
                    || (
                        (
                            parent.Outcome
                            is CodeReviewEvaluationCaseOutcome.Cancelled
                                or CodeReviewEvaluationCaseOutcome.None
                        )
                        && (
                            parent.CandidateStorageUri is not null
                            || parent.CandidateSha256 is not null
                            || parent.CandidateByteCount is not null
                        )
                    )
                )
                    throw new InvalidOperationException("Parent candidate is unavailable.");
            }

            if (reused is not null)
            {
                stage = CodeReviewEvaluationCaseOutcome.JudgeFailed;
                if (item.References.State == CodeReviewEvaluationReferenceState.Unlabelled)
                    throw new InvalidOperationException(
                        "An unlabelled case cannot reuse a candidate for grading."
                    );
                operation = "StartGrading";
                await results.StartGradingAsync(
                    new(org, run.Id, item.CaseId, leaseToken, reused),
                    cancellationToken
                );
            }
            else
            {
                operation = "StartReview";
                await results.StartReviewAsync(
                    new(org, run.Id, item.CaseId, leaseToken),
                    cancellationToken
                );
            }
            operation = "ReadSnapshot";
            var snapshot = await artifacts.ReadSnapshotAsync(
                org,
                run.RepositoryId,
                item.Snapshot,
                cancellationToken
            );
            if (
                snapshot.OrganizationId != org
                || snapshot.RepositoryId != run.RepositoryId
                || snapshot.PullRequestNumber != item.PullRequestNumber
                || snapshot.BaseSha != item.BaseSha
                || snapshot.HeadSha != item.HeadSha
            )
                throw new InvalidOperationException(
                    "Snapshot identity does not match the manifest."
                );
            string xml;
            IReadOnlyList<CodeReviewEvaluationCandidate> parsed;
            if (reused is not null)
            {
                operation = "ReadCandidate";
                xml = await artifacts.ReadCandidateOutputAsync(
                    org,
                    run.RepositoryId,
                    reused,
                    cancellationToken
                );
                parsed = ParseCandidates(xml);
            }
            else
            {
                var config = JsonSerializer.Deserialize<CodeRepositoryReviewConfiguration>(
                    manifest.RepositoryConfigurationJson,
                    JsonOptions
                )
                    ?? throw new InvalidOperationException(
                        "Captured repository configuration is invalid."
                    );
                var reviewer = manifest.ReviewerConfiguration;
                var runtimeAgent = new CodeReviewerRuntimeAgent(
                    manifest.AgentId,
                    reviewer.DisplayName,
                    reviewer.ReviewFacet,
                    reviewer.ModelTier,
                    reviewer.Prompt,
                    reviewer.ActivationConfiguration
                );
                var input = new CodeReviewEvaluationInput(
                    snapshot.ToPullRequestSnapshot(),
                    config,
                    [runtimeAgent],
                    manifest.LibraryIds
                );
                var inScope = CodeReviewFileFilterEvaluator
                    .Apply(input.Snapshot.Files, config.FileFilter)
                    .InScopeFiles;
                var skip =
                    inScope.Count == 0 ? "NoFilesInScope"
                    : !CodeReviewerAgentResolver.IsActivated(
                        reviewer.ActivationConfiguration,
                        inScope
                    )
                        ? "NoAgentActivation"
                    : null;
                if (skip is not null)
                {
                    operation = "RecordSkip";
                    await results.RecordSkipAsync(
                        new(org, run.Id, item.CaseId, leaseToken, skip),
                        cancellationToken
                    );
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                var review = new CodeReviewRecord
                {
                    Id = $"evaluation_{run.Id}_{item.CaseId}",
                    OrganizationId = org,
                    TeamId = run.TeamId,
                    PullRequestRecordId = item.CaseId,
                    RepositoryId = run.RepositoryId,
                    OwnerQualifiedRepoName = snapshot.OwnerQualifiedRepoName,
                    PullRequestNumber = item.PullRequestNumber,
                    Branch = snapshot.HeadSha,
                    Title = snapshot.Title,
                    AuthorLogin = snapshot.AuthorLogin,
                    Status = CodeReviewStatus.Running,
                    RequestOrigin = CodeReviewRequestOrigin.Manual,
                    RemainingReviewBudget = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                var message = new CodeReviewRunRequested
                {
                    OrganizationId = org,
                    TeamId = run.TeamId,
                    RepositoryId = run.RepositoryId,
                    OwnerQualifiedRepoName = snapshot.OwnerQualifiedRepoName,
                    PullRequestNumber = item.PullRequestNumber,
                    PullRequestRecordId = item.CaseId,
                    PullRequestCreatedAtUtc = now,
                    CodeReviewRecordId = review.Id,
                    CodeReviewCreatedAtUtc = now,
                    GitHubDeliveryId = run.Id,
                    TraceContext = new ZeeqTraceContext(null, null),
                };
                operation = "Review";
                var execution = await engine.RunAsync(
                    message,
                    review,
                    CodeReviewExecutionOptions.Evaluation(manifest.ReviewerSelection) with
                    {
                        FixturePullRequestNumber = item.PullRequestNumber,
                    },
                    null,
                    cancellationToken,
                    input
                );
                xml = execution.Xml;
                operation = "PersistCandidate";
                var artifact = await artifacts.WriteCandidateOutputAsync(
                    org,
                    run.RepositoryId,
                    run.Id,
                    item.CaseId,
                    xml,
                    cancellationToken
                );
                var candidates = ParseCandidates(xml);
                parsed = candidates;
                var candidateJson = JsonSerializer.Serialize(candidates, JsonOptions);
                var usage = CodeReviewSourceTelemetrySerializer
                    .Deserialize(execution.SourceTelemetryPayload)
                    ?.TokenUsage;
                var provenance = Provenance(
                    manifest,
                    manifest.ReviewerSelection,
                    CodeReviewAgentExecutor.BuildAgentSystemInstructions(runtimeAgent),
                    execution.SourceTelemetryPayload,
                    isJudge: false
                );
                if (item.References.State == CodeReviewEvaluationReferenceState.Unlabelled)
                {
                    await results.RecordUnlabelledAsync(
                        new(
                            org,
                            run.Id,
                            item.CaseId,
                            leaseToken,
                            artifact,
                            candidates.Count,
                            candidateJson,
                            provenance,
                            usage?.InputTokens,
                            usage?.OutputTokens,
                            review.EstimatedCostUsd
                        ),
                        cancellationToken
                    );
                    return;
                }
                await results.RecordCandidateAsync(
                    new(
                        org,
                        run.Id,
                        item.CaseId,
                        leaseToken,
                        artifact,
                        candidates.Count,
                        candidateJson,
                        provenance,
                        usage?.InputTokens,
                        usage?.OutputTokens,
                        review.EstimatedCostUsd
                    ),
                    cancellationToken
                );
                stage = CodeReviewEvaluationCaseOutcome.JudgeFailed;
                operation = "StartGrading";
                await results.StartGradingAsync(
                    new(org, run.Id, item.CaseId, leaseToken, null),
                    cancellationToken
                );
            }

            operation = "Judge";
            var judged = await judge.GradeAsync(
                org,
                manifest.JudgeSelection,
                snapshot,
                item.References.Findings,
                parsed,
                cancellationToken
            );
            var grade = judged.Grade;
            operation = "PersistGrade";
            await results.RecordGradeAsync(
                new(
                    org,
                    run.Id,
                    item.CaseId,
                    leaseToken,
                    grade.Matches.Count,
                    grade.MissedReferenceIds.Count,
                    item.References.Findings.Count,
                    grade.AdditionalCandidateIds.Count,
                    JsonSerializer.Serialize(grade.Matches, JsonOptions),
                    JsonSerializer.Serialize(
                        new { grade.MissedReferenceIds, grade.AdditionalCandidateIds },
                        JsonOptions
                    ),
                    Provenance(
                        manifest,
                        manifest.JudgeSelection,
                        CodeReviewEvaluationJudge.SystemInstructions,
                        null,
                        isJudge: true,
                        judged.ProviderModelId
                    ),
                    judged.InputTokens,
                    judged.OutputTokens,
                    null
                ),
                cancellationToken
            );
        }
        catch (CodeReviewEvaluationFencedException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCaseFailure(logger, run.Id, item.CaseId, operation, ex.GetType().Name);
            await results.RecordFailureAsync(
                new(
                    org,
                    run.Id,
                    item.CaseId,
                    leaseToken,
                    stage,
                    stage == CodeReviewEvaluationCaseOutcome.ReviewFailed
                        ? "ReviewFailed"
                        : "JudgeFailed",
                    "The evaluation stage failed."
                ),
                cancellationToken
            );
        }
    }

    [LoggerMessage(
        EventId = 3480,
        Level = LogLevel.Warning,
        Message = "Evaluation case failed. RunId={RunId}, CaseId={CaseId}, Operation={Operation}, ErrorType={ErrorType}"
    )]
    private static partial void LogCaseFailure(
        ILogger logger,
        string runId,
        string caseId,
        string operation,
        string errorType
    );

    private IReadOnlyList<CodeReviewEvaluationCandidate> ParseCandidates(string xml)
    {
        var output = validator.Validate(xml);
        if (!output.IsValid || output.Output is null)
            throw new InvalidOperationException("Invalid candidate output.");
        return
        [
            .. output
                .Output.Reviews.SelectMany(x => x.Findings)
                .Select(
                    (x, i) =>
                        new CodeReviewEvaluationCandidate(
                            $"finding_{i + 1}",
                            x.Summary,
                            x.Details,
                            x.Level,
                            x.File,
                            x.Line > 0 ? x.Line : null
                        )
                ),
        ];
    }

    private static string Provenance(
        CodeReviewEvaluationRunManifest manifest,
        CodeReviewEvaluationModelSelection model,
        string actualPrompt,
        string? sourceTelemetry,
        bool isJudge,
        string? providerModelId = null
    )
    {
        var actualBuild = typeof(CodeReviewEvaluationCaseExecutor)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "unavailable";
        var actualHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(actualPrompt))
        );
        var sharedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CodeReviewSystemPrompt.CommonInstructions))
        );
        return JsonSerializer.Serialize(
            new
            {
                model.Tier,
                model.Provider,
                model.Model,
                model.Endpoint,
                model.CredentialSource,
                admittedBuild = manifest.AdmittedBuildVersion,
                actualBuild,
                admittedSystemPromptHash = manifest.SharedSystemPromptHash,
                actualSystemPrompt = actualPrompt,
                actualSystemPromptHash = actualHash,
                contextChanged = actualBuild != manifest.AdmittedBuildVersion
                    || (
                        isJudge
                            ? manifest.JudgeRubricVersion != CodeReviewEvaluationJudge.RubricVersion
                            : sharedHash != manifest.SharedSystemPromptHash
                    ),
                providerModelVersion = providerModelId,
                sourceTelemetry,
            },
            JsonOptions
        );
    }
}
