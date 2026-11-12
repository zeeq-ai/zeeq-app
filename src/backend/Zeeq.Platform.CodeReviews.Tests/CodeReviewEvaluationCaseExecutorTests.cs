using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEvaluationCaseExecutorTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledParentWithoutCompleteCandidateDoesNotGrade(bool partialCandidate)
    {
        var run = new CodeReviewEvaluationRun
        {
            Id = "child",
            ParentRunId = "parent",
            OrganizationId = "org",
            RepositoryId = "repo",
            DatasetId = "dataset",
            DatasetVersion = 1,
            AgentId = "agent",
            ManifestJson = "{}",
            RequestedByUserId = "user",
            RequestKey = "retry",
            State = CodeReviewEvaluationRunState.Running,
        };
        var parent = new CodeReviewEvaluationCaseResult
        {
            Id = "result",
            OrganizationId = "org",
            RunId = "parent",
            ManifestCaseId = "case",
            PullRequestNumber = 901,
            Phase = CodeReviewEvaluationCasePhase.Pending,
            Outcome = CodeReviewEvaluationCaseOutcome.Cancelled,
            CandidateStorageUri = partialCandidate ? "uri" : null,
        };
        var item = new CodeReviewEvaluationManifestCase(
            "case",
            901,
            "title",
            new("uri", "hash", 1),
            "base",
            "head",
            DateTimeOffset.UtcNow,
            1,
            0,
            new("agent", CodeReviewEvaluationReferenceState.ReviewedClean, [])
        );
        var results = Substitute.For<ICodeReviewEvaluationCaseResultStore>();
        results.FindAsync("org", "parent", "case", Arg.Any<CancellationToken>()).Returns(parent);
        var artifacts = Substitute.For<ICodeReviewEvaluationArtifactStore>();
        artifacts
            .ReadSnapshotAsync("org", "repo", item.Snapshot, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<CodeReviewEvaluationSnapshot>(
                    new InvalidOperationException("stop after branch")
                )
            );
        var executor = new CodeReviewEvaluationCaseExecutor(
            results,
            artifacts,
            null!,
            null!,
            null!,
            NullLogger<CodeReviewEvaluationCaseExecutor>.Instance
        );

        await executor.ExecuteAsync(run, null!, item, "lease", CancellationToken.None);

        if (partialCandidate)
            await results
                .DidNotReceive()
                .StartReviewAsync(
                    Arg.Any<CodeReviewEvaluationCaseStart>(),
                    Arg.Any<CancellationToken>()
                );
        else
            await results
                .Received(1)
                .StartReviewAsync(
                    Arg.Is<CodeReviewEvaluationCaseStart>(x =>
                        x.RunId == "child" && x.CaseId == "case"
                    ),
                    Arg.Any<CancellationToken>()
                );
        await results
            .DidNotReceive()
            .StartGradingAsync(
                Arg.Any<CodeReviewEvaluationCaseGrading>(),
                Arg.Any<CancellationToken>()
            );
        await results
            .Received(1)
            .RecordFailureAsync(
                Arg.Any<CodeReviewEvaluationCaseFailure>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetryUsesParentCandidateAndLeavesParentUnchanged(bool cancelledBeforeGrading)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new CodeReviewEvaluationRun
        {
            Id = "child",
            ParentRunId = "parent",
            OrganizationId = "org",
            RepositoryId = "repo",
            DatasetId = "dataset",
            DatasetVersion = 1,
            AgentId = "agent",
            ManifestJson = "{}",
            RequestedByUserId = "user",
            RequestKey = "retry",
            State = CodeReviewEvaluationRunState.Running,
        };
        var parent = new CodeReviewEvaluationCaseResult
        {
            Id = "result",
            OrganizationId = "org",
            RunId = "parent",
            ManifestCaseId = "case",
            PullRequestNumber = 901,
            Phase = cancelledBeforeGrading
                ? CodeReviewEvaluationCasePhase.Pending
                : CodeReviewEvaluationCasePhase.Grading,
            Outcome = cancelledBeforeGrading
                ? CodeReviewEvaluationCaseOutcome.Cancelled
                : CodeReviewEvaluationCaseOutcome.JudgeFailed,
            CandidateStorageUri = "parent-uri",
            CandidateSha256 = "parent-hash",
            CandidateByteCount = 123,
            CandidateFindingsJson = "[\"unchanged\"]",
            ReviewerProvenanceJson = "{\"build\":\"parent\"}",
            ErrorCode = "JudgeFailed",
        };
        var before = JsonSerializer.Serialize(parent);
        var reference = new CodeReviewEvaluationReferenceFinding(
            "r1",
            "expected",
            "expected summary",
            CodeReviewEvaluationFindingSeverity.Major,
            "src/File.cs",
            12,
            CodeReviewEvaluationReferenceOrigin.Manual,
            null,
            null,
            "user",
            now
        );
        var item = new CodeReviewEvaluationManifestCase(
            "case",
            901,
            "title",
            new("snapshot-uri", "snapshot-hash", 50),
            "base",
            "head",
            now,
            1,
            0,
            new("agent", CodeReviewEvaluationReferenceState.Expected, [reference])
        );
        var model = new CodeReviewEvaluationModelSelection(
            CodeReviewModelTier.High,
            "provider",
            "model",
            null,
            CodeReviewEvaluationCredentialSource.SystemDefault,
            null
        );
        var config = new CodeReviewerAgentRevisionConfiguration(
            "reviewer",
            "facet",
            CodeReviewModelTier.High,
            "prompt",
            true,
            new()
        );
        var manifest = new CodeReviewEvaluationRunManifest(
            1,
            "org",
            "repo",
            "dataset",
            1,
            "agent",
            "reviewer",
            "facet",
            null,
            config,
            model,
            model,
            "1",
            "system",
            "hash",
            "{}",
            [],
            "build",
            "1",
            "user",
            "fingerprint",
            now,
            "parent",
            [item]
        );
        var snapshot = new CodeReviewEvaluationSnapshot(
            1,
            "org",
            "repo",
            "owner/repo",
            901,
            "title",
            "body",
            "base",
            "head",
            "author",
            "https://example.test/pr/901",
            false,
            PullRequestState.Closed,
            now,
            1,
            [new("src/File.cs", null, CodeReviewFileMutationState.Modified, "+change", false)]
        );
        var xml = CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument
            {
                Reviews =
                [
                    new()
                    {
                        Agent = "reviewer",
                        Facet = "facet",
                        Summary = "summary",
                        Details = "details",
                        Findings =
                        [
                            new()
                            {
                                Level = CodeReviewFindingLevel.Major,
                                File = "src/File.cs",
                                Line = 12,
                                Summary = "candidate",
                                Details = "candidate details",
                            },
                        ],
                    },
                ],
            }
        );
        var results = Substitute.For<ICodeReviewEvaluationCaseResultStore>();
        results.FindAsync("org", "parent", "case", Arg.Any<CancellationToken>()).Returns(parent);
        var artifacts = Substitute.For<ICodeReviewEvaluationArtifactStore>();
        artifacts
            .ReadSnapshotAsync("org", "repo", item.Snapshot, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        var parentArtifact = new CodeReviewEvaluationArtifact("parent-uri", "parent-hash", 123);
        artifacts
            .ReadCandidateOutputAsync("org", "repo", parentArtifact, Arg.Any<CancellationToken>())
            .Returns(xml);
        var judge = Substitute.For<ICodeReviewEvaluationJudge>();
        judge
            .GradeAsync(
                "org",
                model,
                snapshot,
                Arg.Any<IReadOnlyList<CodeReviewEvaluationReferenceFinding>>(),
                Arg.Any<IReadOnlyList<CodeReviewEvaluationCandidate>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationJudgeResult(
                    new([new("r1", "finding_1", "same issue")], [], []),
                    null,
                    null,
                    null
                )
            );
        // A null engine ensures this path cannot call the reviewer without failing the test.
        var executor = new CodeReviewEvaluationCaseExecutor(
            results,
            artifacts,
            null!,
            new(),
            judge,
            NullLogger<CodeReviewEvaluationCaseExecutor>.Instance
        );

        await executor.ExecuteAsync(run, manifest, item, "lease", CancellationToken.None);

        await results
            .Received(1)
            .StartGradingAsync(
                Arg.Is<CodeReviewEvaluationCaseGrading>(x =>
                    x.RunId == "child"
                    && x.CaseId == "case"
                    && x.LeaseToken == "lease"
                    && x.ReusedCandidate == parentArtifact
                ),
                Arg.Any<CancellationToken>()
            );
        await judge
            .Received(1)
            .GradeAsync(
                "org",
                model,
                snapshot,
                Arg.Is<IReadOnlyList<CodeReviewEvaluationReferenceFinding>>(x =>
                    x.Count == 1 && x[0].Id == "r1"
                ),
                Arg.Is<IReadOnlyList<CodeReviewEvaluationCandidate>>(x =>
                    x.Count == 1 && x[0].Summary == "candidate"
                ),
                Arg.Any<CancellationToken>()
            );
        await results
            .Received(1)
            .RecordGradeAsync(
                Arg.Is<CodeReviewEvaluationCaseGrade>(x =>
                    x.RunId == "child" && x.CaseId == "case" && x.MatchedReferenceCount == 1
                ),
                Arg.Any<CancellationToken>()
            );
        await results
            .DidNotReceive()
            .StartReviewAsync(
                Arg.Any<CodeReviewEvaluationCaseStart>(),
                Arg.Any<CancellationToken>()
            );
        await results
            .DidNotReceive()
            .RecordFailureAsync(
                Arg.Any<CodeReviewEvaluationCaseFailure>(),
                Arg.Any<CancellationToken>()
            );
        await artifacts
            .DidNotReceive()
            .WriteCandidateOutputAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
        await Assert.That(JsonSerializer.Serialize(parent)).IsEqualTo(before);
    }

    [Test]
    public async Task StageFailureLogsOnlySafeMetadataAndRecordsReviewFailure()
    {
        var run = new CodeReviewEvaluationRun
        {
            Id = "run",
            OrganizationId = "org",
            RepositoryId = "repo",
            DatasetId = "dataset",
            DatasetVersion = 1,
            AgentId = "agent",
            ManifestJson = "{}",
            RequestedByUserId = "user",
            RequestKey = "key",
            State = CodeReviewEvaluationRunState.Running,
        };
        var item = new CodeReviewEvaluationManifestCase(
            "case",
            901,
            "title",
            new("uri", "hash", 1),
            "base",
            "head",
            DateTimeOffset.UtcNow,
            1,
            0,
            CodeReviewEvaluationReferenceSet.Unlabelled("agent")
        );
        var results = Substitute.For<ICodeReviewEvaluationCaseResultStore>();
        var artifacts = Substitute.For<ICodeReviewEvaluationArtifactStore>();
        artifacts
            .ReadSnapshotAsync("org", "repo", item.Snapshot, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<CodeReviewEvaluationSnapshot>(
                    new InvalidOperationException("secret prompt and api key")
                )
            );
        var logger = new CapturingLogger();
        var executor = new CodeReviewEvaluationCaseExecutor(
            results,
            artifacts,
            null!,
            null!,
            null!,
            logger
        );

        await executor.ExecuteAsync(run, null!, item, "token", CancellationToken.None);

        await results
            .Received(1)
            .RecordFailureAsync(
                Arg.Is<CodeReviewEvaluationCaseFailure>(x =>
                    x.Outcome == CodeReviewEvaluationCaseOutcome.ReviewFailed
                    && x.ErrorCode == "ReviewFailed"
                ),
                Arg.Any<CancellationToken>()
            );
        await Assert.That(logger.Message).Contains("RunId=run");
        await Assert.That(logger.Message).Contains("CaseId=case");
        await Assert.That(logger.Message).Contains("Operation=ReadSnapshot");
        await Assert.That(logger.Message).Contains("ErrorType=InvalidOperationException");
        await Assert.That(logger.Message.Contains("secret")).IsFalse();
        await Assert.That(logger.Exception).IsNull();
    }

    private sealed class CapturingLogger : ILogger<CodeReviewEvaluationCaseExecutor>
    {
        public string Message { get; private set; } = "";
        public Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Message = formatter(state, exception);
            Exception = exception;
        }
    }
}
