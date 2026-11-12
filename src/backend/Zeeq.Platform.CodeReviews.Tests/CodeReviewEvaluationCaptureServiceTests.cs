using Microsoft.Extensions.Time.Testing;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Unit tests for evaluation capture validation and the snapshot projection.
/// </summary>
/// <remarks>
/// These are the input-trust invariants: a staged capture must describe the requested parent,
/// must be complete, must respect the size limit, and must never write an artifact when
/// validation fails. The projection is checked separately because it is what the reviewer engine
/// actually sees.
///
/// Run this class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewEvaluationCaptureServiceTests/*"
/// </remarks>
public sealed class CodeReviewEvaluationCaptureServiceTests
{
    private const string OrganizationId = "org_1";
    private const string RepositoryId = "repo_1";
    private const string OwnerQualifiedName = "zeeq-fixture/alpha";
    private const int PullRequestNumber = 901;

    private static readonly CodeReviewSettings Settings = new()
    {
        ReviewRequestLinkEncryptionKey = "unit-test-capture-service-key",
    };

    [Test]
    public async Task CaptureAsync_RejectsSnapshotOverTheSizeLimitWithoutWritingArtifact()
    {
        var oversized = NewSnapshot(
            files:
            [
                File("src/Big.cs", new string('x', CodeReviewEvaluationLimits.MaxSnapshotBytes + 1)),
            ],
            declaredChangedFileCount: 1
        );
        var artifacts = new RecordingArtifactStore();
        var service = NewService(oversized, artifacts);

        var exception = await Assert.ThrowsAsync<CodeReviewEvaluationCaptureException>(() =>
            service.CaptureAsync(
                OrganizationId,
                RepositoryId,
                OwnerQualifiedName,
                "dataset_1",
                1,
                PullRequestNumber,
                CancellationToken.None
            )
        );

        await Assert.That(exception!.Code).IsEqualTo("snapshot_too_large");
        await Assert.That(artifacts.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task CaptureAsync_RejectsFileCountMismatchWithoutWritingArtifact()
    {
        var truncated = NewSnapshot(
            files: [File("src/Search.cs", "@@ -1 +1 @@")],
            declaredChangedFileCount: 2
        );
        var artifacts = new RecordingArtifactStore();
        var service = NewService(truncated, artifacts);

        var exception = await Assert.ThrowsAsync<CodeReviewEvaluationCaptureException>(() =>
            service.CaptureAsync(
                OrganizationId,
                RepositoryId,
                OwnerQualifiedName,
                "dataset_1",
                1,
                PullRequestNumber,
                CancellationToken.None
            )
        );

        await Assert.That(exception!.Code).IsEqualTo("snapshot_incomplete");
        await Assert.That(artifacts.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task CaptureAsync_RejectsMissingHeadIdentityWithoutWritingArtifact()
    {
        var missingHead = NewSnapshot(
            files: [File("src/Search.cs", "@@ -1 +1 @@")],
            declaredChangedFileCount: 1,
            headSha: string.Empty
        );
        var artifacts = new RecordingArtifactStore();
        var service = NewService(missingHead, artifacts);

        var exception = await Assert.ThrowsAsync<CodeReviewEvaluationCaptureException>(() =>
            service.CaptureAsync(
                OrganizationId,
                RepositoryId,
                OwnerQualifiedName,
                "dataset_1",
                1,
                PullRequestNumber,
                CancellationToken.None
            )
        );

        await Assert.That(exception!.Code).IsEqualTo("snapshot_incomplete");
        await Assert.That(artifacts.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task CaptureAsync_RejectsSnapshotForAnotherParentBeforeWritingArtifact()
    {
        var foreign = NewSnapshot(
            files: [File("src/Search.cs", "@@ -1 +1 @@")],
            declaredChangedFileCount: 1,
            repositoryId: "repo_other"
        );
        var artifacts = new RecordingArtifactStore();
        var service = NewService(foreign, artifacts);

        var exception = await Assert.ThrowsAsync<CodeReviewEvaluationCaptureException>(() =>
            service.CaptureAsync(
                OrganizationId,
                RepositoryId,
                OwnerQualifiedName,
                "dataset_1",
                1,
                PullRequestNumber,
                CancellationToken.None
            )
        );

        await Assert.That(exception!.Code).IsEqualTo("snapshot_identity_mismatch");
        await Assert.That(artifacts.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task CaptureAsync_PreservesOmissionFlagsAndProjectsWithoutOmittedFilesOrFeedback()
    {
        var snapshot = NewSnapshot(
            files:
            [
                File("src/Search.cs", "@@ -1 +1 @@"),
                new("assets/logo.png", null, CodeReviewFileMutationState.Binary, string.Empty, true),
            ],
            declaredChangedFileCount: 2
        );
        var artifacts = new RecordingArtifactStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var tokens = new CodeReviewEvaluationCaptureTokenProtector(Settings, clock);
        var service = new CodeReviewEvaluationCaptureService(
            new FakeSource(snapshot),
            artifacts,
            tokens
        );

        var outcome = await service.CaptureAsync(
            OrganizationId,
            RepositoryId,
            OwnerQualifiedName,
            "dataset_1",
            4,
            PullRequestNumber,
            CancellationToken.None
        );

        await Assert.That(artifacts.Writes).IsEqualTo(1);
        await Assert.That(outcome.Capture.ChangedFileCount).IsEqualTo(1);
        await Assert.That(outcome.Capture.OmittedFileCount).IsEqualTo(1);
        await Assert.That(outcome.Capture.PullRequestNumber).IsEqualTo(PullRequestNumber);
        await Assert.That(outcome.Capture.AuthorLogin).IsEqualTo("alice");

        // The projection the reviewer engine receives excludes omitted files and never carries
        // developer feedback.
        var projected = snapshot.ToPullRequestSnapshot();
        await Assert.That(projected.Files.Count).IsEqualTo(1);
        await Assert.That(projected.Files.Single().Path).IsEqualTo("src/Search.cs");
        await Assert.That(projected.DeveloperFeedbackComments.Count).IsEqualTo(0);

        // The token carries the staged capture and its dataset binding.
        await Assert.That(tokens.TryUnprotect(outcome.CaptureToken, out var payload)).IsTrue();
        await Assert.That(payload!.DatasetId).IsEqualTo("dataset_1");
        await Assert.That(payload.ExpectedDatasetVersion).IsEqualTo(4);
        await Assert.That(payload.Capture.Snapshot.Sha256).IsEqualTo(outcome.Capture.Snapshot.Sha256);
    }

    private static CodeReviewEvaluationCaptureService NewService(
        CodeReviewEvaluationSnapshot snapshot,
        RecordingArtifactStore artifacts
    )
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        return new(
            new FakeSource(snapshot),
            artifacts,
            new CodeReviewEvaluationCaptureTokenProtector(Settings, clock)
        );
    }

    private static CodeReviewEvaluationSnapshot NewSnapshot(
        IReadOnlyList<CodeReviewEvaluationSnapshotFile> files,
        int declaredChangedFileCount,
        string? headSha = null,
        string repositoryId = RepositoryId
    ) =>
        new(
            CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
            OrganizationId,
            repositoryId,
            OwnerQualifiedName,
            PullRequestNumber,
            "Exact-match ranking regression",
            "Body",
            "base-sha",
            headSha ?? "head-sha",
            "alice",
            "https://github.com/zeeq-fixture/alpha/pull/901",
            false,
            PullRequestState.Open,
            new DateTimeOffset(2026, 11, 12, 9, 59, 0, TimeSpan.Zero),
            declaredChangedFileCount,
            files
        );

    private static CodeReviewEvaluationSnapshotFile File(string path, string patch) =>
        new(path, null, CodeReviewFileMutationState.Modified, patch, false);

    private sealed class FakeSource(CodeReviewEvaluationSnapshot snapshot)
        : ICodeReviewEvaluationPullRequestCaptureSource
    {
        public Task<CodeReviewEvaluationSnapshot> CaptureAsync(
            CodeReviewEvaluationCaptureRequest request,
            CancellationToken cancellationToken
        ) => Task.FromResult(snapshot);
    }

    private sealed class RecordingArtifactStore : ICodeReviewEvaluationArtifactStore
    {
        public int Writes { get; private set; }

        public Task<CodeReviewEvaluationArtifact> WriteSnapshotAsync(
            string organizationId,
            string repositoryId,
            CodeReviewEvaluationSnapshot snapshot,
            CancellationToken cancellationToken
        )
        {
            Writes++;

            var bytes = CodeReviewEvaluationSnapshotSerializer.Serialize(snapshot);

            return Task.FromResult(
                new CodeReviewEvaluationArtifact(
                    "postgres://reviewer-evaluations/org_1/repo_1/snap/snapshot.json",
                    Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(bytes)
                    ),
                    bytes.Length
                )
            );
        }

        public Task<CodeReviewEvaluationArtifact> WriteCandidateOutputAsync(
            string organizationId,
            string repositoryId,
            string runId,
            string caseId,
            string findingsXml,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeReviewEvaluationSnapshot> ReadSnapshotAsync(
            string organizationId,
            string repositoryId,
            CodeReviewEvaluationArtifact artifact,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<string> ReadCandidateOutputAsync(
            string organizationId,
            string repositoryId,
            CodeReviewEvaluationArtifact artifact,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
