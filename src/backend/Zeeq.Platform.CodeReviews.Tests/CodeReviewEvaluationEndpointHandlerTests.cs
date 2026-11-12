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

/// <summary>
/// Handler unit tests for evaluation dataset, capture and PR target endpoints.
/// </summary>
/// <remarks>
/// These cover the boundary rules that are cheap to get wrong and expensive to notice: capture
/// tokens must bind to the pull request they install on, cursor input must not reach the
/// database, and removed membership must stay selectable for re-add. Store behavior itself is
/// covered by the Postgres integration suite.
///
/// Run this class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewEvaluationEndpointHandlerTests/*"
/// </remarks>
public sealed class CodeReviewEvaluationEndpointHandlerTests
{
    [Test]
    public async Task Save_RejectsCaptureTokenForAnotherPullRequest()
    {
        var fixture = Fixture.Create();
        var dataset = fixture.Dataset;
        var token = fixture.ProtectCapture(pullRequestNumber: 901);

        var result = await fixture.SaveHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            dataset.Id,
            new(
                dataset.Version,
                dataset.Name,
                null,
                [
                    new(
                        CaseId: null,
                        PullRequestNumber: 902,
                        CaptureToken: token,
                        ReferenceSets: null
                    ),
                ]
            ),
            Fixture.User(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("capture_token_mismatch");
        await Assert
            .That(
                fixture
                    .Datasets.ReceivedCalls()
                    .Any(call => call.GetMethodInfo().Name == "SaveAsync")
            )
            .IsFalse();
    }

    [Test]
    public async Task Save_ReportsExpiredCaptureTokenDistinctly()
    {
        var fixture = Fixture.Create();
        var dataset = fixture.Dataset;
        var token = fixture.ProtectCapture(pullRequestNumber: 901);
        fixture.Clock.Advance(
            CodeReviewEvaluationLimits.CaptureTokenLifetime + TimeSpan.FromMinutes(1)
        );

        var result = await fixture.SaveHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            dataset.Id,
            new(dataset.Version, dataset.Name, null, [new(null, 901, token, null)]),
            Fixture.User(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("capture_token_expired");
    }

    [Test]
    public async Task Save_WhenFeatureDisabled_ReturnsServiceUnavailable()
    {
        var fixture = Fixture.Create(evaluationsEnabled: false);

        var result = await fixture.SaveHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            fixture.Dataset.Id,
            new(fixture.Dataset.Version, "Name", null, []),
            Fixture.User(),
            CancellationToken.None
        );

        var json = result.Result as JsonHttpResult<CodeReviewEndpointError>;

        await Assert.That(json).IsNotNull();
        await Assert.That(json!.StatusCode).IsEqualTo(StatusCodes.Status503ServiceUnavailable);
        await Assert.That(json.Value!.Code).IsEqualTo("feature_disabled");
    }

    [Test]
    public async Task ListTargets_RejectsMalformedCursorWithoutQueryingStore()
    {
        var fixture = Fixture.Create();

        var result = await fixture.ListTargetsHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            query: null,
            datasetId: null,
            cursor: "not-a-cursor",
            pageSize: 25,
            Fixture.User(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_request");
        await Assert
            .That(
                fixture
                    .TargetStore.ReceivedCalls()
                    .Any(call => call.GetMethodInfo().Name == "SearchAsync")
            )
            .IsFalse();
    }

    [Test]
    public async Task ListTargets_DoesNotTreatRemovedMembershipAsAlreadyAdded()
    {
        var fixture = Fixture.Create();
        fixture
            .TargetStore.SearchAsync(
                Arg.Any<CodeReviewEvaluationPrTargetQuery>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationPrTargetPage(
                    [
                        new(
                            "pr_1",
                            DateTimeOffset.UtcNow,
                            901,
                            "Exact-match ranking regression",
                            PullRequestState.Open,
                            "head-sha",
                            "alice",
                            DateTimeOffset.UtcNow
                        ),
                    ],
                    null,
                    null
                )
            );

        var result = await fixture.ListTargetsHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            query: null,
            datasetId: fixture.Dataset.Id,
            cursor: null,
            pageSize: 25,
            Fixture.User(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewEvaluationPrTargetListResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Items.Single().AlreadyInDataset).IsFalse();

        // Removed membership must stay selectable for re-add.
        await fixture
            .Datasets.Received(1)
            .ListCasesAsync(
                Fixture.OrganizationId,
                fixture.Dataset.Id,
                false,
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task Resolve_WithMemberRole_ReturnsForbid()
    {
        var fixture = Fixture.Create(role: "member");

        var result = await fixture.ResolveHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            new(fixture.Dataset.Id, fixture.Dataset.Version, ["901"]),
            Fixture.User(),
            CancellationToken.None
        );

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
    }

    [Test]
    public async Task GetCaseSnapshot_WithMemberRole_ReturnsForbid()
    {
        var fixture = Fixture.Create(role: "member");

        var result = await fixture.SnapshotHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            fixture.Dataset.Id,
            fixture.Case.Id,
            Fixture.User(),
            CancellationToken.None
        );

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
        await Assert
            .That(
                fixture
                    .Artifacts.ReceivedCalls()
                    .Any(call => call.GetMethodInfo().Name == "ReadSnapshotAsync")
            )
            .IsFalse();
    }

    [Test]
    public async Task GetCaseSnapshot_WithUnknownCase_ReturnsNotFound()
    {
        var fixture = Fixture.Create();

        var result = await fixture.SnapshotHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            fixture.Dataset.Id,
            "crec_missing",
            Fixture.User(),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
        await Assert
            .That(
                fixture
                    .Artifacts.ReceivedCalls()
                    .Any(call => call.GetMethodInfo().Name == "ReadSnapshotAsync")
            )
            .IsFalse();
    }

    [Test]
    public async Task GetCaseSnapshot_ReturnsStoredArtifactFromServerRow()
    {
        var fixture = Fixture.Create();

        var result = await fixture.SnapshotHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            fixture.Dataset.Id,
            fixture.Case.Id,
            Fixture.User(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewEvaluationCaseSnapshotResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Snapshot.HeadSha).IsEqualTo("head-sha");
        await Assert.That(ok.Value.Snapshot.Files.Single().Patch).IsEqualTo("@@ -1 +1 @@");

        // The artifact path must come from the case row, never from the request.
        await fixture
            .Artifacts.Received(1)
            .ReadSnapshotAsync(
                Fixture.OrganizationId,
                Fixture.RepositoryId,
                Arg.Is<CodeReviewEvaluationArtifact>(artifact =>
                    artifact.StorageUri == fixture.Case.SnapshotStorageUri
                    && artifact.Sha256 == fixture.Case.SnapshotSha256
                    && artifact.ByteCount == fixture.Case.SnapshotByteCount
                ),
                Arg.Any<CancellationToken>()
            );
    }

    private sealed class Fixture
    {
        public const string OrganizationId = "org_123";
        public const string RepositoryId = "repo_123";

        private Fixture(CodeReviewSettings settings, IZeeqMembershipStore memberships)
        {
            Settings = settings;
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
            Datasets
                .FindAsync(OrganizationId, Dataset.Id, true, Arg.Any<CancellationToken>())
                .Returns(Dataset);
            Datasets
                .FindCaseAsync(
                    OrganizationId,
                    Dataset.Id,
                    Case.Id,
                    true,
                    Arg.Any<CancellationToken>()
                )
                .Returns(Case);
            Artifacts = Substitute.For<ICodeReviewEvaluationArtifactStore>();
            Artifacts
                .ReadSnapshotAsync(
                    OrganizationId,
                    RepositoryId,
                    Arg.Any<CodeReviewEvaluationArtifact>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Snapshot);
            Datasets
                .ListCasesAsync(
                    OrganizationId,
                    Dataset.Id,
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult<IReadOnlyList<CodeReviewEvaluationCase>>([]));

            CaptureTokens = new CodeReviewEvaluationCaptureTokenProtector(settings, Clock);
            TargetStore = Substitute.For<ICodeReviewEvaluationPrTargetStore>();
            TargetService = new(
                TargetStore,
                new CodeReviewEvaluationCaptureService(
                    Substitute.For<ICodeReviewEvaluationPullRequestCaptureSource>(),
                    Substitute.For<ICodeReviewEvaluationArtifactStore>(),
                    CaptureTokens
                )
            );

            SaveHandler = new(
                authorization,
                repositories,
                Datasets,
                CaptureTokens,
                settings,
                Clock
            );
            ListTargetsHandler = new(authorization, repositories, Datasets, TargetService);
            ResolveHandler = new(authorization, repositories, Datasets, TargetService, settings);
            SnapshotHandler = new(authorization, repositories, Datasets, Artifacts);
        }

        public CodeReviewSettings Settings { get; }

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public CodeReviewEvaluationCaptureTokenProtector CaptureTokens { get; }

        public ICodeReviewEvaluationDatasetStore Datasets { get; }

        public ICodeReviewEvaluationPrTargetStore TargetStore { get; }

        public ICodeReviewEvaluationArtifactStore Artifacts { get; }

        public GetCodeReviewEvaluationCaseSnapshotHandler SnapshotHandler { get; }

        public CodeReviewEvaluationPrTargetService TargetService { get; }

        public SaveCodeReviewEvaluationDatasetHandler SaveHandler { get; }

        public ListCodeReviewEvaluationPrTargetsHandler ListTargetsHandler { get; }

        public ResolveCodeReviewEvaluationPrTargetsHandler ResolveHandler { get; }

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

        public string ProtectCapture(int pullRequestNumber) =>
            CaptureTokens.Protect(
                CaptureTokens.CreatePayload(
                    OrganizationId,
                    RepositoryId,
                    Dataset.Id,
                    Dataset.Version,
                    new CodeReviewEvaluationStagedCapture(
                        new CodeReviewEvaluationArtifact(
                            "postgres://reviewer-evaluations/org_123/repo_123/snap/snapshot.json",
                            new string('a', 64),
                            128
                        ),
                        pullRequestNumber,
                        "Captured title",
                        "alice",
                        "base-sha",
                        "head-sha",
                        PullRequestState.Open,
                        Clock.GetUtcNow(),
                        ChangedFileCount: 1,
                        OmittedFileCount: 0,
                        DeclaredChangedFileCount: 1,
                        PullRequestRecordId: null
                    )
                )
            );

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
