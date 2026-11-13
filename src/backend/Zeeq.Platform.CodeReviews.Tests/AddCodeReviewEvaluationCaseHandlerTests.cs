using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>Tests add-case concurrency and preservation behavior.</summary>
public sealed class AddCodeReviewEvaluationCaseHandlerTests
{
    [Test]
    public async Task Duplicate_WithStaleVersion_DoesNotCaptureOrSave()
    {
        var f = Fixture.Create();
        f.Datasets.ListCasesAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([f.Case]));
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 1),
            Fixture.User(),
            CancellationToken.None
        );
        var ok = result.Result as Ok<AddCodeReviewEvaluationCaseResponse>;
        await Assert.That(ok!.Value!.Added).IsFalse();
        await Assert.That(ok.Value.HeadSha).IsEqualTo(f.Case.HeadSha);
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
        await f.Datasets.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Test]
    public async Task StaleVersion_DoesNotCapture()
    {
        var f = Fixture.Create();
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 1),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is Conflict<CodeReviewEndpointError>).IsTrue();
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
    }

    [Test]
    public async Task Add_PreservesExistingCaseAndReturnsCapture()
    {
        var f = Fixture.Create();
        f.Case.PullRequestTitle = "Existing";
        var oldCase = new CodeReviewEvaluationCase
        {
            Id = "old",
            OrganizationId = Fixture.OrganizationId,
            RepositoryId = Fixture.RepositoryId,
            DatasetId = f.Dataset.Id,
            PullRequestNumber = 900,
            PullRequestTitle = "Old",
            SnapshotStorageUri = "old",
            SnapshotSha256 = "old",
            BaseSha = "old",
            HeadSha = "old",
            CapturedAtUtc = DateTimeOffset.UtcNow,
        };
        f.Datasets.ListCasesAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([oldCase]));
        CodeReviewEvaluationDatasetSaveRequest? saved = null;
        f.Datasets.SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                saved = call.Arg<CodeReviewEvaluationDatasetSaveRequest>();
                return new CodeReviewEvaluationDatasetSaveResult(
                    CodeReviewEvaluationDatasetSaveOutcome.Saved,
                    f.Dataset,
                    []
                );
            });
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        var ok = result.Result as Ok<AddCodeReviewEvaluationCaseResponse>;
        await Assert.That(ok!.Value!.Added).IsTrue();
        await Assert.That(ok.Value.HeadSha).IsEqualTo(f.Snapshot.HeadSha);
        await Assert.That(saved!.Cases.Count).IsEqualTo(2);
        await Assert.That(saved.Cases[0].CaseId).IsEqualTo(oldCase.Id);
        await Assert.That(saved.Cases[0].ReferenceSets).IsNull();
        await Assert.That(saved.Cases[1].Capture).IsNotNull();
        await Assert.That(saved.Cases[1].ReferenceSets).IsNull();
    }

    [Test]
    public async Task Member_IsForbidden()
    {
        var f = Fixture.Create(role: "member");
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
    }

    [Test]
    public async Task Conflict_WithConcurrentSamePr_ReturnsWinnerWithoutRetry()
    {
        var f = Fixture.Create();
        f.Datasets.ListCasesAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([]),
                Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([f.Case])
            );
        f.Datasets.SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationDatasetSaveResult(
                    CodeReviewEvaluationDatasetSaveOutcome.VersionConflict,
                    null,
                    null
                )
            );
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        var ok = result.Result as Ok<AddCodeReviewEvaluationCaseResponse>;
        await Assert.That(ok!.Value!.Added).IsFalse();
        await Assert.That(ok.Value.HeadSha).IsEqualTo(f.Case.HeadSha);
        await f
            .Datasets.Received(1)
            .SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task Conflict_WithoutConcurrentSamePr_ReturnsConflictWithoutRetry()
    {
        var f = Fixture.Create();
        f.Datasets.SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationDatasetSaveResult(
                    CodeReviewEvaluationDatasetSaveOutcome.VersionConflict,
                    null,
                    null
                )
            );
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is Conflict<CodeReviewEndpointError>).IsTrue();
        await f
            .Datasets.Received(1)
            .SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task CaptureFailure_DoesNotSave()
    {
        var f = Fixture.Create();
        f.Source.CaptureAsync(
                Arg.Any<CodeReviewEvaluationCaptureRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
                Task.FromException<CodeReviewEvaluationSnapshot>(
                    new CodeReviewEvaluationCaptureException("pr_not_found", "Not found")
                )
            );
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        var bad = result.Result as BadRequest<CodeReviewEndpointError>;
        await Assert.That(bad!.Value!.Code).IsEqualTo("pr_not_found");
        await f.Datasets.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Test]
    public async Task FeatureDisabled_StopsBeforeCapture()
    {
        var f = Fixture.Create(evaluationsEnabled: false);
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert
            .That((result.Result as JsonHttpResult<CodeReviewEndpointError>)!.StatusCode)
            .IsEqualTo(503);
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
    }

    [Test]
    public async Task InvalidPr_StopsBeforeCapture()
    {
        var f = Fixture.Create();
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(0, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is BadRequest<CodeReviewEndpointError>).IsTrue();
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
    }

    [Test]
    public async Task FullDataset_RejectsNewPrBeforeCapture()
    {
        var f = Fixture.Create();
        var cases = Enumerable
            .Range(1, CodeReviewEvaluationLimits.MaxCases)
            .Select(n => new CodeReviewEvaluationCase
            {
                Id = $"case_{n}",
                OrganizationId = Fixture.OrganizationId,
                RepositoryId = Fixture.RepositoryId,
                DatasetId = f.Dataset.Id,
                PullRequestNumber = n,
                PullRequestTitle = "Old",
                SnapshotStorageUri = "old",
                SnapshotSha256 = "old",
                BaseSha = "old",
                HeadSha = "old",
                CapturedAtUtc = DateTimeOffset.UtcNow,
            })
            .ToArray();
        f.Datasets.ListCasesAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>(cases));
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert
            .That((result.Result as BadRequest<CodeReviewEndpointError>)!.Value!.Code)
            .IsEqualTo("case_limit_reached");
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
    }

    [Test]
    public async Task MissingOrArchivedDataset_ReturnsNotFound()
    {
        var f = Fixture.Create();
        f.Datasets.FindAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns((CodeReviewEvaluationDataset?)null);
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is NotFound).IsTrue();
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
    }

    [Test]
    public async Task DuplicateAtCapacity_IsNoOp()
    {
        var f = Fixture.Create();
        var cases = Enumerable.Repeat(f.Case, CodeReviewEvaluationLimits.MaxCases).ToArray();
        f.Datasets.ListCasesAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>(cases));
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 1),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert
            .That((result.Result as Ok<AddCodeReviewEvaluationCaseResponse>)!.Value!.Added)
            .IsFalse();
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
        await f.Datasets.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Test]
    public async Task WrongRepositoryDataset_IsNotFound()
    {
        var f = Fixture.Create();
        var other = new CodeReviewEvaluationDataset
        {
            Id = f.Dataset.Id,
            OrganizationId = Fixture.OrganizationId,
            RepositoryId = "other",
            Name = "other",
            CreateRequestKey = "key",
        };
        f.Datasets.FindAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(other);
        var result = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is NotFound).IsTrue();
        await f
            .Datasets.DidNotReceiveWithAnyArgs()
            .ListCasesAsync(default!, default!, default, default);
        await f.Source.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default);
    }

    [Test]
    public async Task LostResponse_ReplayAndRemoval_NeverWritesAgain()
    {
        var f = Fixture.Create();
        f.Datasets.SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                f.Dataset.Version = 4;
                return new CodeReviewEvaluationDatasetSaveResult(
                    CodeReviewEvaluationDatasetSaveOutcome.Saved,
                    f.Dataset,
                    []
                );
            });
        var first = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert
            .That((first.Result as Ok<AddCodeReviewEvaluationCaseResponse>)!.Value!.Added)
            .IsTrue();
        f.Datasets.ListCasesAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([f.Case]));
        var replay = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert
            .That((replay.Result as Ok<AddCodeReviewEvaluationCaseResponse>)!.Value!.Added)
            .IsFalse();
        f.Dataset.Version = 5;
        f.Datasets.ListCasesAsync(
                Fixture.OrganizationId,
                f.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([]));
        var removed = await f.AddHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(901, 3),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(removed.Result is Conflict<CodeReviewEndpointError>).IsTrue();
        await f
            .Datasets.Received(1)
            .SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            );
        await f
            .Source.Received(1)
            .CaptureAsync(
                Arg.Any<CodeReviewEvaluationCaptureRequest>(),
                Arg.Any<CancellationToken>()
            );
    }

    private sealed class Fixture
    {
        public const string OrganizationId = "org_123";
        public const string RepositoryId = "repo_123";

        private Fixture(CodeReviewSettings settings, IZeeqMembershipStore memberships)
        {
            var authorization = new CodeReviewAuthorization(memberships);
            var repositories = Substitute.For<ICodeRepositoryStore>();
            repositories
                .FindActiveForOrganizationAsync(
                    OrganizationId,
                    RepositoryId,
                    Arg.Any<CancellationToken>()
                )
                .Returns(
                    new CodeRepository
                    {
                        Id = RepositoryId,
                        OrganizationId = OrganizationId,
                        Provider = "github",
                        OwnerQualifiedName = "zeeq-fixture/alpha",
                        DisplayName = "Fixture",
                        Enabled = true,
                        CreatedAtUtc = Clock.GetUtcNow(),
                        UpdatedAtUtc = Clock.GetUtcNow(),
                    }
                );

            Datasets = Substitute.For<ICodeReviewEvaluationDatasetStore>();
            Datasets
                .FindAsync(OrganizationId, Dataset.Id, false, Arg.Any<CancellationToken>())
                .Returns(Dataset);
            Artifacts = Substitute.For<ICodeReviewEvaluationArtifactStore>();
            Datasets
                .ListCasesAsync(
                    OrganizationId,
                    Dataset.Id,
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([]));

            CaptureTokens = new CodeReviewEvaluationCaptureTokenProtector(settings, Clock);
            Source = Substitute.For<ICodeReviewEvaluationPullRequestCaptureSource>();
            Source
                .CaptureAsync(
                    Arg.Any<CodeReviewEvaluationCaptureRequest>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Snapshot);
            Artifacts
                .WriteSnapshotAsync(
                    OrganizationId,
                    RepositoryId,
                    Arg.Any<CodeReviewEvaluationSnapshot>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(new CodeReviewEvaluationArtifact("path", new string('a', 64), 128));
            AddHandler = new(
                authorization,
                repositories,
                Datasets,
                new CodeReviewEvaluationCaptureService(Source, Artifacts, CaptureTokens),
                settings
            );
        }

        public ICodeReviewEvaluationPullRequestCaptureSource Source { get; }
        public AddCodeReviewEvaluationCaseHandler AddHandler { get; }
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public CodeReviewEvaluationCaptureTokenProtector CaptureTokens { get; }

        public ICodeReviewEvaluationDatasetStore Datasets { get; }

        public ICodeReviewEvaluationArtifactStore Artifacts { get; }

        public CodeReviewEvaluationCase Case { get; } =
            new()
            {
                Id = "crec_fixture",
                OrganizationId = OrganizationId,
                RepositoryId = RepositoryId,
                DatasetId = "cred_fixture",
                PullRequestNumber = 901,
                PullRequestTitle = "Captured title",
                SnapshotStorageUri =
                    "postgres://reviewer-evaluations/org_123/repo_123/snap/snapshot.json",
                SnapshotSha256 = new string('a', 64),
                SnapshotByteCount = 128,
                BaseSha = "base-sha",
                HeadSha = "head-sha",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

        public CodeReviewEvaluationSnapshot Snapshot { get; } =
            new(
                CodeReviewEvaluationSnapshot.CurrentSchemaVersion,
                OrganizationId,
                RepositoryId,
                "zeeq-fixture/alpha",
                901,
                "Captured title",
                "Captured body",
                "base-sha",
                "head-sha",
                "alice",
                "https://github.com/zeeq-fixture/alpha/pull/901",
                false,
                PullRequestState.Open,
                DateTimeOffset.UtcNow,
                1,
                [
                    new(
                        "src/Search.cs",
                        null,
                        CodeReviewFileMutationState.Modified,
                        "@@ -1 +1 @@",
                        false
                    ),
                ]
            );

        public CodeReviewEvaluationDataset Dataset { get; } =
            new()
            {
                Id = "cred_fixture",
                OrganizationId = OrganizationId,
                RepositoryId = RepositoryId,
                Name = "Fixture dataset",
                Version = 3,
                CreateRequestKey = "fixture-key",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

        public static Fixture Create(string role = "admin", bool evaluationsEnabled = true)
        {
            var memberships = Substitute.For<IZeeqMembershipStore>();
            memberships
                .ListActiveMembershipsForUserAsync("usr_123", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                        new()
                        {
                            Id = "mem_123",
                            OrganizationId = OrganizationId,
                            UserId = "usr_123",
                            Role = role,
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "usr_123",
                        },
                    ])
                );

            return new(
                new CodeReviewSettings
                {
                    EvaluationsEnabled = evaluationsEnabled,
                    ReviewRequestLinkEncryptionKey = "unit-test-evaluation-key",
                },
                memberships
            );
        }

        public static ClaimsPrincipal User() => TestUser();
    }

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(OpenIddictConstants.Claims.Subject, "usr_123"),
                    new Claim(ClaimTypes.Name, "test@example.test"),
                ],
                authenticationType: "test"
            )
        );
}
