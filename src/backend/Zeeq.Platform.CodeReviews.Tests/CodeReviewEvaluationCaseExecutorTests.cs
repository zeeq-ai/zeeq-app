using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEvaluationCaseExecutorTests
{
    [Test]
    [Arguments(CodeReviewEvaluationCaseOutcome.Cancelled, false)]
    [Arguments(CodeReviewEvaluationCaseOutcome.Cancelled, true)]
    [Arguments(CodeReviewEvaluationCaseOutcome.None, false)]
    [Arguments(CodeReviewEvaluationCaseOutcome.None, true)]
    public async Task InterruptedParentWithoutCompleteCandidateDoesNotGrade(
        CodeReviewEvaluationCaseOutcome outcome,
        bool partialCandidate
    )
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
            Outcome = outcome,
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
    [Arguments(CodeReviewEvaluationCaseOutcome.JudgeFailed)]
    [Arguments(CodeReviewEvaluationCaseOutcome.Cancelled)]
    [Arguments(CodeReviewEvaluationCaseOutcome.None)]
    public async Task RetryUsesParentCandidateAndLeavesParentUnchanged(
        CodeReviewEvaluationCaseOutcome outcome
    )
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
            Phase =
                outcome == CodeReviewEvaluationCaseOutcome.JudgeFailed
                    ? CodeReviewEvaluationCasePhase.Grading
                    : CodeReviewEvaluationCasePhase.Pending,
            Outcome = outcome,
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
                    new([new("r1", "finding_1", "same issue")], [], [], [], [], 0),
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
                    x.RunId == "child"
                    && x.CaseId == "case"
                    && x.MatchedReferenceCount == 1
                    && x.JudgeProvenanceJson.Contains("\"contextChanged\":true")
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
    [Arguments(
        CodeReviewEvaluationReferenceState.Unlabelled,
        CodeReviewEvaluationCaseOutcome.FalsePositiveChecked
    )]
    [Arguments(
        CodeReviewEvaluationReferenceState.ReviewedClean,
        CodeReviewEvaluationCaseOutcome.Graded
    )]
    public async Task RejectedReference_RetryRecordsCorrectOutcome(
        CodeReviewEvaluationReferenceState state,
        CodeReviewEvaluationCaseOutcome expectedOutcome
    )
    {
        var outcome = CodeReviewEvaluationCaseOutcome.JudgeFailed;
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
            Phase =
                outcome == CodeReviewEvaluationCaseOutcome.JudgeFailed
                    ? CodeReviewEvaluationCasePhase.Grading
                    : CodeReviewEvaluationCasePhase.Pending,
            Outcome = outcome,
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
            now,
            CodeReviewEvaluationReferenceDisposition.Rejected
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
            new("agent", state, [reference])
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
                    new(
                        [
                            new(
                                "r1",
                                "finding_1",
                                "same issue",
                                CodeReviewEvaluationReferenceDisposition.Rejected
                            ),
                        ],
                        [],
                        ["r1"],
                        [],
                        [],
                        0
                    ),
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
                    x.RunId == "child"
                    && x.CaseId == "case"
                    && x.Outcome == expectedOutcome
                    && x.MatchedReferenceCount == 0
                    && x.RepeatedFalsePositiveCount == 1
                    && x.TrackedFalsePositiveCount == 1
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
    [Arguments(CodeReviewEvaluationReferenceState.Unlabelled, true, true, false)]
    [Arguments(CodeReviewEvaluationReferenceState.Unlabelled, true, true, true)]
    [Arguments(CodeReviewEvaluationReferenceState.Unlabelled, false, true, false)]
    [Arguments(CodeReviewEvaluationReferenceState.ReviewedClean, true, true, false)]
    [Arguments(CodeReviewEvaluationReferenceState.ReviewedClean, true, false, false)]
    public async Task FreshReview_HandlesIndependentFalsePositiveAxis(
        CodeReviewEvaluationReferenceState state,
        bool rejected,
        bool hasCandidate,
        bool includeTriage
    )
    {
        var now = DateTimeOffset.UtcNow;
        var run = new CodeReviewEvaluationRun
        {
            Id = "child",
            ParentRunId = null,
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
            now,
            CodeReviewEvaluationReferenceDisposition.Rejected
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
            new("agent", state, rejected ? [reference] : []),
            includeTriage
                ? new KnownFalsePositiveSnapshot("<known_false_positives />", "model", 1, [])
                : null
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
            [item],
            includeTriage
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
        if (!hasCandidate)
            xml = CodeReviewXmlOutputValidator.Serialize(
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
                            Findings = [],
                        },
                    ],
                }
            );
        var results = Substitute.For<ICodeReviewEvaluationCaseResultStore>();
        var artifacts = Substitute.For<ICodeReviewEvaluationArtifactStore>();
        artifacts
            .ReadSnapshotAsync("org", "repo", item.Snapshot, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        artifacts
            .WriteCandidateOutputAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new CodeReviewEvaluationArtifact("uri", "hash", 1));
        var reviewer = new TestCodeReviewAgentExecutor { Xml = xml };
        var engine = new CodeReviewExecutionEngine(
            Substitute.For<ICodeReviewPullRequestSource>(),
            Substitute.For<ICodeRepositoryStore>(),
            Substitute.For<IPullRequestRecordStore>(),
            new(
                Substitute.For<ICodeReviewerAgentStore>(),
                NullLogger<CodeReviewerAgentResolver>.Instance
            ),
            reviewer,
            Substitute.For<ICodeReviewPreviousReviewStore>(),
            new(),
            Substitute.For<ILibraryDocumentStore>(),
            new TestHybridCache(),
            NullLogger<CodeReviewExecutionEngine>.Instance,
            Substitute.For<ICodeReviewFindingTriageStore>()
        );
        ICodeReviewEvaluationJudge judgeForTest = hasCandidate
            ? Substitute.For<ICodeReviewEvaluationJudge>()
            : new CodeReviewEvaluationJudge(null!);
        if (hasCandidate)
            judgeForTest
                .GradeAsync(
                    Arg.Any<string>(),
                    Arg.Any<CodeReviewEvaluationModelSelection>(),
                    Arg.Any<CodeReviewEvaluationSnapshot>(),
                    Arg.Any<IReadOnlyList<CodeReviewEvaluationReferenceFinding>>(),
                    Arg.Any<IReadOnlyList<CodeReviewEvaluationCandidate>>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(
                    new CodeReviewEvaluationJudgeResult(
                        new([], [], [], ["r1"], ["finding_1"], 0),
                        null,
                        null,
                        null
                    )
                );
        var executor = new CodeReviewEvaluationCaseExecutor(
            results,
            artifacts,
            engine,
            new(),
            judgeForTest,
            NullLogger<CodeReviewEvaluationCaseExecutor>.Instance
        );

        await executor.ExecuteAsync(run, manifest, item, "lease", CancellationToken.None);
        await Assert
            .That(reviewer.Options!.PriorContext?.KnownFalsePositives)
            .IsEqualTo(includeTriage ? "<known_false_positives />" : null);
        await Assert.That(reviewer.PreviousReviews.Count).IsEqualTo(0);

        if (!rejected)
        {
            await results
                .Received(1)
                .RecordUnlabelledAsync(
                    Arg.Any<CodeReviewEvaluationCaseUnlabelled>(),
                    Arg.Any<CancellationToken>()
                );
            await judgeForTest
                .DidNotReceive()
                .GradeAsync(
                    Arg.Any<string>(),
                    Arg.Any<CodeReviewEvaluationModelSelection>(),
                    Arg.Any<CodeReviewEvaluationSnapshot>(),
                    Arg.Any<IReadOnlyList<CodeReviewEvaluationReferenceFinding>>(),
                    Arg.Any<IReadOnlyList<CodeReviewEvaluationCandidate>>(),
                    Arg.Any<CancellationToken>()
                );
        }
        else
        {
            var expected =
                state == CodeReviewEvaluationReferenceState.Unlabelled
                    ? CodeReviewEvaluationCaseOutcome.FalsePositiveChecked
                    : CodeReviewEvaluationCaseOutcome.Graded;
            await results
                .Received(1)
                .RecordGradeAsync(
                    Arg.Is<CodeReviewEvaluationCaseGrade>(x =>
                        x.Outcome == expected && x.TrackedFalsePositiveCount == 1
                    ),
                    Arg.Any<CancellationToken>()
                );
        }
        await results
            .DidNotReceive()
            .RecordFailureAsync(
                Arg.Any<CodeReviewEvaluationCaseFailure>(),
                Arg.Any<CancellationToken>()
            );
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
