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
    public async Task PromoteTriage_RejectsOrganizationMemberWithoutDatasetMutation()
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships.ListActiveMembershipsForUserAsync("user-1", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<OrganizationMembership>>([
                new OrganizationMembership
                {
                    Id = "membership-1",
                    OrganizationId = "org-1",
                    UserId = "user-1",
                    Role = "member",
                    Status = MembershipStatus.Active,
                    CreatedByUserId = "user-1",
                },
            ]);
        var datasets = Substitute.For<ICodeReviewEvaluationDatasetStore>();
        var handler = new PromoteFindingTriageHandler(
            new CodeReviewAuthorization(memberships),
            Substitute.For<ICodeRepositoryStore>(),
            datasets,
            Substitute.For<ICodeReviewFindingTriageStore>()
        );
        var user = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(OpenIddictConstants.Claims.Subject, "user-1"),
        ], "test"));

        var result = await handler.HandleAsync("org-1", "repo-1", "dataset-1", "triage-1", new("agent-1", 1), user, CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<ForbidHttpResult>();
        await datasets.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Test]
    public async Task PromoteTriage_PreservesOtherCasesAndReferenceSetsInVersionedSave()
    {
        var setup = CreatePromotionSetup(role: "admin", datasetVersion: 3);
        CodeReviewEvaluationDatasetSaveRequest? savedRequest = null;
        setup.Datasets.SaveAsync(Arg.Do<CodeReviewEvaluationDatasetSaveRequest>(value => savedRequest = value), Arg.Any<CancellationToken>())
            .Returns(new CodeReviewEvaluationDatasetSaveResult(CodeReviewEvaluationDatasetSaveOutcome.Saved, setup.Dataset, [setup.TargetCase, setup.OtherCase]));

        var result = await setup.Handler.HandleAsync("org-1", "repo-1", "dataset-1", "triage-1", new("agent-1", 3), setup.User, CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<Microsoft.AspNetCore.Http.HttpResults.Ok<CodeReviewEvaluationDatasetResponse>>();
        await Assert.That(savedRequest).IsNotNull();
        await Assert.That(savedRequest!.Cases.Count).IsEqualTo(2);
        var promoted = savedRequest.Cases.Single(item => item.CaseId == "case-target");
        var untouched = savedRequest.Cases.Single(item => item.CaseId == "case-other");
        await Assert.That(untouched.ReferenceSets).IsEquivalentTo(setup.OtherCase.ReferenceSets);
        var rejected = promoted.ReferenceSets!.Single(set => set.ReviewerAgentId == "agent-1").Findings.Single();
        await Assert.That(rejected.Disposition).IsEqualTo(CodeReviewEvaluationReferenceDisposition.Rejected);
        await Assert.That(rejected.Origin).IsEqualTo(CodeReviewEvaluationReferenceOrigin.HistoricalReview);
        await Assert.That(rejected.SourceFindingId).IsEqualTo("review-1#F2");
        await Assert.That(rejected.File).IsEqualTo("(unknown file)");
        await Assert.That(savedRequest.ExpectedVersion).IsEqualTo(3);
    }

    [Test]
    public async Task PromoteTriage_StaleVersionReturnsConflictWithoutSaving()
    {
        var setup = CreatePromotionSetup(role: "owner", datasetVersion: 4);

        var result = await setup.Handler.HandleAsync("org-1", "repo-1", "dataset-1", "triage-1", new("agent-1", 3), setup.User, CancellationToken.None);

        await Assert.That(result.Result).IsTypeOf<Microsoft.AspNetCore.Http.HttpResults.Conflict<CodeReviewEndpointError>>();
        await setup.Datasets.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    private static (PromoteFindingTriageHandler Handler, ICodeReviewEvaluationDatasetStore Datasets, ClaimsPrincipal User, CodeReviewEvaluationDataset Dataset, CodeReviewEvaluationCase TargetCase, CodeReviewEvaluationCase OtherCase) CreatePromotionSetup(string role, long datasetVersion)
    {
        const string userId = "user-1";
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships.ListActiveMembershipsForUserAsync(userId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<OrganizationMembership>>([
            new OrganizationMembership { Id = "membership-1", OrganizationId = "org-1", UserId = userId, Role = role, Status = MembershipStatus.Active, CreatedByUserId = userId },
        ]);
        var repos = Substitute.For<ICodeRepositoryStore>();
        repos.FindActiveForOrganizationAsync("org-1", "repo-1", Arg.Any<CancellationToken>()).Returns(new CodeRepository
        {
            Id = "repo-1", OrganizationId = "org-1", Provider = "github", OwnerQualifiedName = "owner/repo", DisplayName = "repo", Enabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        var dataset = new CodeReviewEvaluationDataset { Id = "dataset-1", OrganizationId = "org-1", RepositoryId = "repo-1", Name = "Fixture", CreateRequestKey = "key", Version = datasetVersion };
        var target = new CodeReviewEvaluationCase { Id = "case-target", OrganizationId = "org-1", RepositoryId = "repo-1", DatasetId = "dataset-1", PullRequestNumber = 88, PullRequestTitle = "target", SnapshotStorageUri = "snap://target", SnapshotSha256 = new string('a', 64), BaseSha = "base", HeadSha = "head", CapturedAtUtc = DateTimeOffset.UtcNow };
        var other = new CodeReviewEvaluationCase { Id = "case-other", OrganizationId = "org-1", RepositoryId = "repo-1", DatasetId = "dataset-1", PullRequestNumber = 89, PullRequestTitle = "other", SnapshotStorageUri = "snap://other", SnapshotSha256 = new string('b', 64), BaseSha = "base", HeadSha = "head", CapturedAtUtc = DateTimeOffset.UtcNow, ReferenceSets = [new("agent-1", CodeReviewEvaluationReferenceState.Expected, [new("existing", "Existing", "Keep me", CodeReviewEvaluationFindingSeverity.Major, "src/a.cs", 4, CodeReviewEvaluationReferenceOrigin.Manual, null, null, userId, DateTimeOffset.UtcNow)])] };
        var datasets = Substitute.For<ICodeReviewEvaluationDatasetStore>();
        datasets.FindAsync("org-1", "dataset-1", false, Arg.Any<CancellationToken>()).Returns(dataset);
        datasets.ListCasesAsync("org-1", "dataset-1", false, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<CodeReviewEvaluationCase>>([target, other]);
        var triage = new CodeReviewFindingTriage { Id = "triage-1", OrganizationId = "org-1", RepositoryId = "repo-1", OwnerQualifiedRepoName = "owner/repo", PullRequestNumber = 88, SourceReviewId = "review-1", SourceReviewCreatedAtUtc = DateTimeOffset.UtcNow, OccurrenceKey = "F2", SemanticId = "semantic-finding", Facet = "quality", FindingSummary = "finding summary", FilePath = null, Severity = CodeReviewFindingLevel.Major, ApprovalState = CodeReviewFindingApprovalState.Confirmed, Disposition = CodeReviewFindingDisposition.FalsePositive, CurrentReason = "not a defect" };
        var triages = Substitute.For<ICodeReviewFindingTriageStore>();
        triages.FindAsync("org-1", "triage-1", Arg.Any<CancellationToken>()).Returns(triage);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, userId)], "test"));
        return (new(new CodeReviewAuthorization(memberships), repos, datasets, triages), datasets, user, dataset, target, other);
    }

    [Test]
    public async Task Create_ReplayedKeyFromAnotherRepository_ReturnsConflict()
    {
        var fixture = Fixture.Create();
        fixture
            .Datasets.CreateAsync(
                Arg.Any<CodeReviewEvaluationDataset>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<Task<CodeReviewEvaluationDataset>>(_ =>
                throw new CodeReviewEvaluationDatasetRequestKeyConflictException(
                    "shared-key",
                    "repo_other"
                )
            );

        var result = await fixture.CreateHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            new("Fixture dataset", null, "shared-key"),
            Fixture.User(),
            CancellationToken.None
        );

        var conflict = result.Result as Conflict<CodeReviewEndpointError>;

        await Assert.That(conflict).IsNotNull();
        await Assert.That(conflict!.Value!.Code).IsEqualTo("create_request_key_conflict");
    }

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

    private static CodeReviewEvaluationReferenceFindingDto Finding(
        CodeReviewEvaluationReferenceDisposition disposition =
            CodeReviewEvaluationReferenceDisposition.Rejected,
        string? rationale = "  explanation  "
    ) =>
        new(
            "ref",
            "title",
            "summary",
            CodeReviewEvaluationFindingSeverity.Minor,
            "src/A.cs",
            null,
            CodeReviewEvaluationReferenceOrigin.Manual,
            null,
            null,
            disposition,
            rationale
        );

    [Test]
    [Arguments(CodeReviewEvaluationReferenceState.Unlabelled)]
    [Arguments(CodeReviewEvaluationReferenceState.Expected)]
    [Arguments(CodeReviewEvaluationReferenceState.ReviewedClean)]
    public async Task Save_RejectedReferencePersistsTrimmedRationale(
        CodeReviewEvaluationReferenceState state
    )
    {
        var f = Fixture.Create();
        f.Datasets.SaveAsync(
                Arg.Any<CodeReviewEvaluationDatasetSaveRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CodeReviewEvaluationDatasetSaveResult(
                    CodeReviewEvaluationDatasetSaveOutcome.Saved,
                    f.Dataset,
                    []
                )
            );
        var result = await f.SaveHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(
                f.Dataset.Version,
                f.Dataset.Name,
                null,
                [new(f.Case.Id, 901, null, [new("agent", state, [Finding()])])]
            ),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is Ok<CodeReviewEvaluationDatasetResponse>).IsTrue();
        await f
            .Datasets.Received(1)
            .SaveAsync(
                Arg.Is<CodeReviewEvaluationDatasetSaveRequest>(x =>
                    x.Cases.Single().ReferenceSets!.Single().Findings.Single().Disposition
                        == CodeReviewEvaluationReferenceDisposition.Rejected
                    && x.Cases.Single().ReferenceSets!.Single().Findings.Single().Rationale
                        == "explanation"
                ),
                Arg.Any<CancellationToken>()
            );
        var mapped = CodeReviewEvaluationEndpointMapping.ToDto(
            CodeReviewEvaluationEndpointMapping.ToModel(
                new("agent", state, [Finding()]),
                "user",
                DateTimeOffset.UnixEpoch
            )
        );
        await Assert.That(mapped.Findings.Single().Rationale).IsEqualTo("explanation");
    }

    [Test]
    [Arguments(CodeReviewEvaluationReferenceState.Unlabelled)]
    [Arguments(CodeReviewEvaluationReferenceState.ReviewedClean)]
    public async Task Save_RejectsExpectedReferenceInUnauditedState(
        CodeReviewEvaluationReferenceState state
    )
    {
        var f = Fixture.Create();
        var result = await f.SaveHandler.HandleAsync(
            Fixture.OrganizationId,
            Fixture.RepositoryId,
            f.Dataset.Id,
            new(
                f.Dataset.Version,
                f.Dataset.Name,
                null,
                [
                    new(
                        f.Case.Id,
                        901,
                        null,
                        [
                            new(
                                "agent",
                                state,
                                [Finding(CodeReviewEvaluationReferenceDisposition.Expected)]
                            ),
                        ]
                    ),
                ]
            ),
            Fixture.User(),
            CancellationToken.None
        );
        await Assert
            .That((result.Result as BadRequest<CodeReviewEndpointError>)!.Value!.Code)
            .IsEqualTo("reference_state_conflict");
    }

    [Test]
    public async Task Save_RejectsInvalidDispositionRationaleAndCombinedLimit()
    {
        var f = Fixture.Create();
        async Task<string?> Save(IReadOnlyList<CodeReviewEvaluationReferenceFindingDto> findings)
        {
            var result = await f.SaveHandler.HandleAsync(
                Fixture.OrganizationId,
                Fixture.RepositoryId,
                f.Dataset.Id,
                new(
                    f.Dataset.Version,
                    f.Dataset.Name,
                    null,
                    [
                        new(
                            f.Case.Id,
                            901,
                            null,
                            [new("agent", CodeReviewEvaluationReferenceState.Expected, findings)]
                        ),
                    ]
                ),
                Fixture.User(),
                CancellationToken.None
            );
            return (result.Result as BadRequest<CodeReviewEndpointError>)?.Value?.Code;
        }
        await Assert
            .That(await Save([Finding((CodeReviewEvaluationReferenceDisposition)42)]))
            .IsEqualTo("reference_disposition_invalid");
        await Assert
            .That(await Save([Finding(rationale: new string('x', 1001))]))
            .IsEqualTo("reference_rationale_too_long");
        await Assert
            .That(
                await Save(
                    Enumerable
                        .Range(0, 101)
                        .Select(i =>
                            Finding(
                                i % 2 == 0
                                    ? CodeReviewEvaluationReferenceDisposition.Expected
                                    : CodeReviewEvaluationReferenceDisposition.Rejected
                            ) with
                            {
                                Id = i.ToString(),
                            }
                        )
                        .ToArray()
                )
            )
            .IsEqualTo("reference_limit_reached");
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

            CreateHandler = new(authorization, repositories, Datasets, settings, Clock);
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

        public CreateCodeReviewEvaluationDatasetHandler CreateHandler { get; }

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
