using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Unit tests for manual evaluation target resolution.
/// </summary>
/// <remarks>
/// Resolution is per entry: a hostile or malformed URL must fail only that entry. An oversized
/// number in a pull request URL previously overflowed the batch and aborted valid selections.
/// </remarks>
public sealed class CodeReviewEvaluationPrTargetServiceTests
{
    private const string OrganizationId = "org_1";
    private const string RepositoryId = "repo_1";
    private const string OwnerQualifiedName = "zeeq-fixture/alpha";

    [Test]
    public async Task ResolveAsync_RejectsOverflowingUrlNumberButKeepsValidEntries()
    {
        var service = NewService();

        var results = await service.ResolveAsync(
            OrganizationId,
            RepositoryId,
            OwnerQualifiedName,
            "dataset_1",
            1,
            [
                "https://github.com/zeeq-fixture/alpha/pull/99999999999999999999",
                "901",
            ],
            CancellationToken.None
        );

        await Assert.That(results.Count).IsEqualTo(2);

        var overflow = results[0];
        await Assert.That(overflow.ErrorCode).IsEqualTo("pr_invalid_entry");
        await Assert.That(overflow.Capture).IsNull();

        var valid = results[1];
        await Assert.That(valid.ErrorCode).IsNull();
        await Assert.That(valid.PullRequestNumber).IsEqualTo(901);
        await Assert.That(valid.Capture).IsNotNull();
        await Assert.That(valid.Capture!.Capture.PullRequestNumber).IsEqualTo(901);
    }

    [Test]
    public async Task ResolveAsync_RejectsCrossRepositoryUrlPerEntry()
    {
        var service = NewService();

        var results = await service.ResolveAsync(
            OrganizationId,
            RepositoryId,
            OwnerQualifiedName,
            "dataset_1",
            1,
            ["https://github.com/other-owner/other-repo/pull/901"],
            CancellationToken.None
        );

        await Assert.That(results.Single().ErrorCode).IsEqualTo("pr_cross_repository");
    }

    private static CodeReviewEvaluationPrTargetService NewService()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        var settings = new CodeReviewSettings
        {
            ReviewRequestLinkEncryptionKey = "unit-test-pr-target-key",
        };
        var tokens = new CodeReviewEvaluationCaptureTokenProtector(settings, clock);
        var targetStore = Substitute.For<ICodeReviewEvaluationPrTargetStore>();
        targetStore
            .FindAsync(
                OrganizationId,
                RepositoryId,
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<CodeReviewEvaluationPrTargetRecord?>(null));

        var source = Substitute.For<ICodeReviewEvaluationPullRequestCaptureSource>();
        source
            .CaptureAsync(
                Arg.Any<CodeReviewEvaluationCaptureRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                var request = call.Arg<CodeReviewEvaluationCaptureRequest>();
                return Task.FromResult(Snapshot(request.PullRequestNumber));
            });

        var captureService = new CodeReviewEvaluationCaptureService(
            source,
            new RecordingArtifactStore(),
            tokens
        );

        return new(targetStore, captureService);
    }

    private static CodeReviewEvaluationSnapshot Snapshot(int pullRequestNumber) =>
        new(
            CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
            OrganizationId,
            RepositoryId,
            OwnerQualifiedName,
            pullRequestNumber,
            "Captured title",
            "Body",
            "base-sha",
            "head-sha",
            "alice",
            $"https://github.com/{OwnerQualifiedName}/pull/{pullRequestNumber}",
            false,
            PullRequestState.Open,
            DateTimeOffset.UtcNow,
            1,
            [new("src/Search.cs", null, CodeReviewFileMutationState.Modified, "@@ -1 +1 @@", false)]
        );

    private sealed class RecordingArtifactStore : ICodeReviewEvaluationArtifactStore
    {
        public Task<CodeReviewEvaluationArtifact> WriteSnapshotAsync(
            string organizationId,
            string repositoryId,
            CodeReviewEvaluationSnapshot snapshot,
            CancellationToken cancellationToken
        )
        {
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
