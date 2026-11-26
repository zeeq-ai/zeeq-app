using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews.Tests;

public sealed class CodeReviewMcpToolsTests
{
    [Test]
    public async Task TriageReviewFinding_ProposesWithMcpTrustAndReturnsConfirmationLink()
    {
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var row = new CodeReviewFindingTriage
        {
            Id = "cft_123",
            OrganizationId = "org_123",
            OwnerQualifiedRepoName = "owner/repo",
            SourceReviewId = "cr_123",
            SourceReviewCreatedAtUtc = DateTimeOffset.UtcNow,
            OccurrenceKey = "F1",
            SemanticId = "example-finding-name",
            Facet = "Security",
            FindingSummary = "summary",
            ApprovalState = CodeReviewFindingApprovalState.Proposed,
        };
        service
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FindingTriageOutcome>(new FindingTriageOutcome.Applied(row)));
        var opts = Options();
        var links = new CodeReviewRequestLinkFactory(
            opts,
            new CodeReviewRequestTokenProtector(opts.Value.CodeReview)
        );
        var response = await CodeReviewMcpTools.TriageReviewFinding(
            service,
            links,
            null!,
            TestUser(),
            "cr_123",
            "abcde.F1",
            "false_positive",
            "validated elsewhere"
        );
        await Assert.That(response).Contains("/code-reviews/triages/org_123/cft_123");
        await service
            .Received(1)
            .TriageAsync(
                Arg.Is<FindingTriageRequest>(x =>
                    x.OrganizationId == "org_123"
                    && x.Channel == CodeReviewFindingTriageChannel.Mcp
                    && x.Operation == FindingTriageOperation.Set
                    && x.Disposition == CodeReviewFindingDisposition.FalsePositive
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task TriageReviewFinding_RejectsConfirmVerbWithoutCallingService()
    {
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var response = await CodeReviewMcpTools.TriageReviewFinding(
            service,
            null!,
            null!,
            TestUser(),
            "cr_123",
            "abcde.F1",
            "confirm"
        );
        await Assert.That(response).Contains("Invalid");
        await service
            .DidNotReceive()
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Attestation_RequiresActiveMemberAndForwardsExactQuote()
    {
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("usr_123", Arg.Any<CancellationToken>())
            .Returns([
                new OrganizationMembership
                {
                    Id = "membership",
                    OrganizationId = "org_123",
                    UserId = "usr_123",
                    Role = "member",
                    Status = MembershipStatus.Active,
                    CreatedByUserId = "usr_123",
                },
            ]);
        var auth = new CodeReviewAuthorization(memberships);
        service
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new FindingTriageOutcome.Invalid(["test"]));
        const string quote = " Yes, defer this. ";
        await CodeReviewMcpTools.TriageReviewFinding(
            service,
            null!,
            auth,
            TestUser(),
            "cr_123",
            "F1",
            "deferred",
            "reason",
            confirmed: true,
            confirmationQuote: quote
        );
        await service
            .Received(1)
            .TriageAsync(
                Arg.Is<FindingTriageRequest>(x =>
                    x.Channel == CodeReviewFindingTriageChannel.AgentAttested
                    && x.ActorUserId == "usr_123"
                    && x.ConfirmationQuote == quote
                ),
                Arg.Any<CancellationToken>()
            );
        memberships
            .ListActiveMembershipsForUserAsync("usr_123", Arg.Any<CancellationToken>())
            .Returns([]);
        var response = await CodeReviewMcpTools.TriageReviewFinding(
            service,
            null!,
            auth,
            TestUser(),
            "cr_123",
            "F1",
            "deferred",
            "reason",
            confirmed: true,
            confirmationQuote: quote
        );
        await Assert.That(response).Contains("Forbidden");
        await service
            .Received(1)
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("yes\0no")]
    public async Task Attestation_RejectsInvalidQuote(string quote)
    {
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var response = await CodeReviewMcpTools.TriageReviewFinding(
            service,
            null!,
            null!,
            TestUser(),
            "cr_123",
            "F1",
            "deferred",
            "reason",
            confirmed: true,
            confirmationQuote: quote
        );
        await Assert.That(response).Contains("Invalid");
        await service
            .DidNotReceive()
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Attestation_RejectsClientCredentialsAndOversizedQuote()
    {
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var identity = (ClaimsIdentity)TestUser().Identity!;
        identity.AddClaim(
            new Claim(AuthClaims.AuthMode, OpenIddictConstants.GrantTypes.ClientCredentials)
        );
        var response = await CodeReviewMcpTools.TriageReviewFinding(
            service,
            null!,
            null!,
            new ClaimsPrincipal(identity),
            "cr_123",
            "F1",
            "deferred",
            "reason",
            confirmed: true,
            confirmationQuote: "yes"
        );
        await Assert.That(response).Contains("Forbidden");
        response = await CodeReviewMcpTools.TriageReviewFinding(
            service,
            null!,
            null!,
            TestUser(),
            "cr_123",
            "F1",
            "deferred",
            "reason",
            confirmed: true,
            confirmationQuote: new string('x', 501)
        );
        await Assert.That(response).Contains("Invalid");
        await service
            .DidNotReceive()
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AttestationSchema_InjectsAuthorizationInsteadOfExposingIt()
    {
        var services = new ServiceCollection()
            .AddSingleton(Substitute.For<ICodeReviewFindingTriageService>())
            .AddSingleton(new CodeReviewAuthorization(Substitute.For<IZeeqMembershipStore>()))
            .AddSingleton(
                new CodeReviewRequestLinkFactory(
                    Options(),
                    new CodeReviewRequestTokenProtector(Options().Value.CodeReview)
                )
            )
            .BuildServiceProvider();
        var tool = McpServerTool.Create(
            typeof(CodeReviewMcpTools).GetMethod(nameof(CodeReviewMcpTools.TriageReviewFinding))!,
            options: new McpServerToolCreateOptions { Services = services }
        );
        var schema = tool.ProtocolTool.InputSchema.ToString();
        await Assert.That(schema).DoesNotContain("authorization");
        await Assert.That(schema).Contains("confirmationQuote");
    }

    [Test]
    public async Task RunReview_CreateUploadUrl_ReturnsRenderedInstructions()
    {
        var runner = new TestExpertCodeReviewRunner();

        var response = await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "create_upload_url"
        );

        await Assert.That(response).Contains("jobId: job_123");
        await Assert.That(response).Contains("uploadToken: see URL `?token=` parameter");
        await Assert.That(response).Contains("maxDiffSizeBytes: 1234");
        await Assert.That(response).Contains("curl -X PUT --data-binary @/tmp/zeeq-review.diff");
        await Assert.That(response).Contains("?token=token_123");
        await Assert.That(runner.CreateUploadUser).IsNotNull();
    }

    [Test]
    public async Task RunReview_RunReview_WithMissingInput_ReturnsValidationError()
    {
        var response = await CodeReviewMcpTools.RunReview(
            new TestExpertCodeReviewRunner(),
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            remoteRepoNameUsingGit: "owner/repo"
        );

        await Assert.That(response).IsEqualTo("uploadToken is required for run_review.");
    }

    [Test]
    public async Task RunReview_UninitializedRunnerOutcome_ReturnsFailure()
    {
        var runner = new TestExpertCodeReviewRunner { ReturnUninitialized = true };

        var response = await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo"
        );

        await Assert
            .That(response)
            .IsEqualTo("Expert code review failed: Review outcome is uninitialized.");
    }

    [Test]
    public async Task RunReview_WithUnsupportedAction_ReturnsClearError()
    {
        var response = await CodeReviewMcpTools.RunReview(
            new TestExpertCodeReviewRunner(),
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "wat"
        );

        await Assert.That(response).Contains("Unsupported expert_code_review action");
    }

    [Test]
    public async Task RunReview_RunReviewWithoutAuthenticatedUser_ReturnsError()
    {
        var runner = new TestExpertCodeReviewRunner();

        var response = await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            user: null,
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo"
        );

        await Assert
            .That(response)
            .Contains("An authenticated MCP user is required to run an expert code review.");
    }

    [Test]
    public async Task RunReview_RunReviewWithAuthenticatedUser_ThreadsPrincipalToRunner()
    {
        var runner = new TestExpertCodeReviewRunner();
        var user = TestUser();

        await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            user,
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo"
        );

        await Assert.That(runner.RunRequestUser).IsNotNull();
        await Assert
            .That(runner.RunRequestUser!.FindFirstValue(OpenIddictConstants.Claims.Subject))
            .IsEqualTo("usr_123");
    }

    [Test]
    public async Task RunReview_RunReviewWithBranch_ThreadsBranchToRunner()
    {
        var runner = new TestExpertCodeReviewRunner();

        await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo",
            activeOrPlannedBranchName: "feat/branch-first-linking"
        );

        await Assert.That(runner.RunRequest).IsNotNull();
        await Assert.That(runner.RunRequest!.Branch).IsEqualTo("feat/branch-first-linking");
    }

    [Test]
    public async Task RunReview_SliceScope_IsPassedAndIncludesFreshFullReviewNote()
    {
        var runner = new TestExpertCodeReviewRunner();

        var response = await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo",
            scope: "slice"
        );

        await Assert.That(runner.RunRequest!.Scope).IsEqualTo("slice");
        await Assert.That(response).Contains("Omit `reviewGroupId`");
    }

    [Test]
    public async Task RunReview_InvalidScope_ReturnsValidationError()
    {
        var response = await CodeReviewMcpTools.RunReview(
            new TestExpertCodeReviewRunner(),
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo",
            scope: "changed-files"
        );

        await Assert.That(response).IsEqualTo("scope must be either 'full' or 'slice'.");
    }

    [Test]
    public async Task RunReview_FreshFullScope_OmitsFreshReviewNote()
    {
        var response = await CodeReviewMcpTools.RunReview(
            new TestExpertCodeReviewRunner(),
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo"
        );

        await Assert.That(response).DoesNotContain("Before opening or updating the PR");
    }

    [Test]
    public async Task RunReview_RunReviewWithLibraries_ThreadsLibrariesToRunner()
    {
        var runner = new TestExpertCodeReviewRunner();

        await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo",
            libraries: ["zeeq-app", "zeeq-docs"]
        );

        await Assert.That(runner.RunRequest).IsNotNull();
        await Assert.That(runner.RunRequest!.Libraries).IsEquivalentTo(["zeeq-app", "zeeq-docs"]);
    }

    [Test]
    public async Task RunReview_RunReviewWithoutLibraries_ThreadsEmptyLibrariesToRunner()
    {
        var runner = new TestExpertCodeReviewRunner();

        await CodeReviewMcpTools.RunReview(
            runner,
            Options(),
            LoggerFactory.Create(_ => { }),
            TestUser(),
            "run_review",
            jobId: "job_123",
            uploadToken: "token_123",
            remoteRepoNameUsingGit: "owner/repo"
        );

        await Assert.That(runner.RunRequest).IsNotNull();
        await Assert.That(runner.RunRequest!.Libraries).IsEmpty();
    }

    [Test]
    public async Task GetReviewFindings_UsesRawOrdinalsAndFiltersConfirmedTriage()
    {
        var fixture = ReviewFindingsFixture.Create();
        var triages = Substitute.For<ICodeReviewFindingTriageStore>();
        var row = new CodeReviewFindingTriage
        {
            Id = "cft_1",
            OrganizationId = "org_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            SourceReviewId = "cr_123",
            SourceReviewCreatedAtUtc = DateTimeOffset.UtcNow,
            OccurrenceKey = "F2",
            SemanticId = "major-issue-data",
            Facet = "Security",
            FindingSummary = "Major issue",
            Disposition = CodeReviewFindingDisposition.Deferred,
            ApprovalState = CodeReviewFindingApprovalState.Confirmed,
        };
        triages
            .ListForReviewAsync("org_123", "cr_123", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewFindingTriage>>([row]));
        var opts = Options();
        var links = new CodeReviewRequestLinkFactory(
            opts,
            new CodeReviewRequestTokenProtector(opts.Value.CodeReview)
        );
        var all = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            triages,
            links,
            TestUser(),
            "zeeq-ai/zeeq",
            pullRequestNumber: 42,
            minimumLevel: "MAJOR"
        );
        await Assert.That(all).Contains("ref=\"r_123.F2\"");
        await Assert.That(all).Contains("approval=\"Confirmed\"");
        var actionable = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            triages,
            links,
            TestUser(),
            "zeeq-ai/zeeq",
            pullRequestNumber: 42,
            minimumLevel: "MAJOR",
            actionableOnly: true
        );
        await Assert.That(actionable).DoesNotContain("Major issue");
        await Assert.That(actionable).Contains("Critical issue");
    }

    [Test]
    public async Task GetReviewFindings_WithPullRequestAndMinimumMajor_ReturnsCriticalAndMajorFindings()
    {
        var fixture = ReviewFindingsFixture.Create();

        var response = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            new CodeReviewRequestLinkFactory(
                Options(),
                new CodeReviewRequestTokenProtector(Options().Value.CodeReview)
            ),
            TestUser(),
            "zeeq-ai/zeeq",
            pullRequestNumber: 42,
            minimumLevel: "MAJOR"
        );

        await Assert.That(response).Contains("<instruction_for_agents>");
        await Assert.That(response).Contains("criticality=\"Critical\"");
        await Assert.That(response).Contains("criticality=\"Major\"");
        await Assert.That(response).DoesNotContain("criticality=\"Minor\"");
        await Assert.That(fixture.Reviews.LastPullRequestRecordId).IsEqualTo("pr_123");
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetReviewFindings_WithBranch_ReturnsNewestCompletedBranchFindings()
    {
        var fixture = ReviewFindingsFixture.Create();

        var response = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            new CodeReviewRequestLinkFactory(
                Options(),
                new CodeReviewRequestTokenProtector(Options().Value.CodeReview)
            ),
            TestUser(),
            "zeeq-ai/zeeq",
            branch: "feature/review-findings"
        );

        await Assert.That(response).Contains("repo=\"zeeq-ai/zeeq\"");
        await Assert.That(response).Contains("pr=\"42\"");
        await Assert.That(fixture.Reviews.LastBranch).IsEqualTo("feature/review-findings");
    }

    [Test]
    public async Task GetReviewFindings_WithBothPullRequestAndBranch_ReturnsValidationMessage()
    {
        var fixture = ReviewFindingsFixture.Create();

        var response = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            new CodeReviewRequestLinkFactory(
                Options(),
                new CodeReviewRequestTokenProtector(Options().Value.CodeReview)
            ),
            TestUser(),
            "zeeq-ai/zeeq",
            pullRequestNumber: 42,
            branch: "feature/review-findings"
        );

        await Assert
            .That(response)
            .IsEqualTo("Provide either pullRequestNumber or branch, not both.");
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetReviewFindings_WithNumericMinimumLevel_ReturnsValidationMessage()
    {
        var fixture = ReviewFindingsFixture.Create();

        var response = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            new CodeReviewRequestLinkFactory(
                Options(),
                new CodeReviewRequestTokenProtector(Options().Value.CodeReview)
            ),
            TestUser(),
            "zeeq-ai/zeeq",
            pullRequestNumber: 42,
            minimumLevel: "0"
        );

        await Assert
            .That(response)
            .IsEqualTo(
                "minimumLevel must be one of CRITICAL, MAJOR, MINOR, SUGGESTION, or COMMENT."
            );
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetReviewFindings_WithCommentMinimum_ReturnsAllFindings()
    {
        var fixture = ReviewFindingsFixture.Create();

        var response = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            new CodeReviewRequestLinkFactory(
                Options(),
                new CodeReviewRequestTokenProtector(Options().Value.CodeReview)
            ),
            TestUser(),
            "zeeq-ai/zeeq",
            branch: "feature/review-findings",
            minimumLevel: "COMMENT"
        );

        await Assert.That(response).Contains("criticality=\"Critical\"");
        await Assert.That(response).Contains("criticality=\"Major\"");
        await Assert.That(response).Contains("criticality=\"Minor\"");
    }

    [Test]
    public async Task GetReviewFindings_WithStoredFindingsAndZeroCounters_ReturnsStoredFindings()
    {
        var fixture = ReviewFindingsFixture.Create();
        fixture.Reviews.Review.CriticalFindings = 0;
        fixture.Reviews.Review.MajorFindings = 0;
        fixture.Reviews.Review.MinorFindings = 0;

        var response = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            new CodeReviewRequestLinkFactory(
                Options(),
                new CodeReviewRequestTokenProtector(Options().Value.CodeReview)
            ),
            TestUser(),
            "zeeq-ai/zeeq",
            pullRequestNumber: 42
        );

        await Assert.That(response).Contains("criticality=\"Critical\"");
        await Assert.That(response).Contains("criticality=\"Major\"");
        await Assert.That(response).Contains("criticality=\"Minor\"");
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(1);
    }

    private static IOptions<AppSettings> Options() =>
        Microsoft.Extensions.Options.Options.Create(
            new AppSettings { CodeReview = new CodeReviewSettings { DiffUploadMaxBytes = 1234 } }
        );

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(OpenIddictConstants.Claims.Subject, "usr_123"),
                    new Claim(AuthClaims.OrganizationId, "org_123"),
                ],
                authenticationType: "test"
            )
        );

    private sealed class TestExpertCodeReviewRunner : IExpertCodeReviewRunner
    {
        public bool ReturnUninitialized { get; init; }

        public ClaimsPrincipal? CreateUploadUser { get; private set; }

        public ExpertCodeReviewRunRequest? RunRequest { get; private set; }

        public ClaimsPrincipal? RunRequestUser { get; private set; }

        public Task<ExpertCodeReviewUploadUrlResponse> CreateUploadUrlAsync(
            ClaimsPrincipal user,
            CancellationToken cancellationToken = default
        )
        {
            CreateUploadUser = user;

            return Task.FromResult(
                new ExpertCodeReviewUploadUrlResponse(
                    "job_123",
                    "token_123",
                    "http://api.test/api/v1/code-review/mcp-diffs/job_123?token=token_123",
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    "curl -X PUT --data-binary @/tmp/zeeq-review.diff \"http://api.test/api/v1/code-review/mcp-diffs/job_123?token=token_123\""
                )
            );
        }

        public Task<ExpertCodeReviewOutcome<ExpertCodeReviewRunResponse>> RunReviewAsync(
            ExpertCodeReviewRunRequest request,
            ClaimsPrincipal user,
            CancellationToken cancellationToken = default
        )
        {
            RunRequest = request;
            RunRequestUser = user;

            if (ReturnUninitialized)
            {
                return Task.FromResult(
                    default(ExpertCodeReviewOutcome<ExpertCodeReviewRunResponse>)
                );
            }

            return Task.FromResult<ExpertCodeReviewOutcome<ExpertCodeReviewRunResponse>>(
                new ExpertCodeReviewRunResponse(
                    request.JobId,
                    """
                    <reviews noAgentsActivated="false">
                      <review facet="General" agent="Agent">
                        <summary>Summary.</summary>
                        <details>Details.</details>
                        <findings />
                      </review>
                    </reviews>
                    """,
                    ["src/App.cs"],
                    [],
                    ReviewId: "",
                    ReviewCreatedAtUtc: DateTimeOffset.UtcNow,
                    ReviewGroupId: "",
                    ReviewViewUrl: ""
                )
            );
        }
    }

    private sealed class ReviewFindingsFixture
    {
        private ReviewFindingsFixture() { }

        public TestCodeRepositoryStore Repositories { get; } = new();

        public TestPullRequestLookupStore PullRequestLookups { get; } = new();

        public TestCodeReviewRecordStore Reviews { get; } = new();

        public TestCodeReviewArtifactStore Artifacts { get; } = new();

        public static ReviewFindingsFixture Create()
        {
            var fixture = new ReviewFindingsFixture();
            fixture.Artifacts.StoredXml = ReviewXml();

            return fixture;
        }
    }

    private sealed class TestCodeRepositoryStore : ICodeRepositoryStore
    {
        public Task<Zeeq.Core.Models.CodeRepository?> UpdateSettingsWithLibrariesAsync(
            string organizationId,
            string repositoryId,
            Zeeq.Platform.CodeReviews.RepositorySettingsPatch patch,
            string[]? libraryIds,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<Zeeq.Core.Models.CodeRepository> UpsertWithLibrariesAsync(
            Zeeq.Core.Models.CodeRepository repository,
            string[]? libraryIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Zeeq.Core.Models.CodeRepository?> ReplaceRepositoryLibrariesAsync(
            string organizationId,
            string repositoryId,
            string[] libraryIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task ReplaceLibraryRepositoriesAsync(
            string organizationId,
            string libraryId,
            string[] repositoryIds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public CodeRepository Repository { get; } =
            new()
            {
                Id = "repo_123",
                OrganizationId = "org_123",
                Provider = "github",
                OwnerQualifiedName = "zeeq-ai/zeeq",
                DisplayName = "zeeq-ai/zeeq",
                Enabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            };

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                provider == "github" && ownerQualifiedName == Repository.OwnerQualifiedName
                    ? (CodeRepository?)Repository
                    : null
            );

        // The prompt-token lookup is exercised by the MCP dynamic-prompt tests, not this fixture.

        public Task<CodeRepository?> FindConfiguredForOrganizationByProviderIdentityAsync(
            string organizationId,
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository?> FindActiveForOrganizationByProviderIdentityAsync(
            string organizationId,
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                organizationId == Repository.OrganizationId
                && provider == "github"
                && ownerQualifiedName == Repository.OwnerQualifiedName
                && Repository.Enabled
                && Repository.DisabledAtUtc is null
                    ? (CodeRepository?)Repository
                    : null
            );

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class TestPullRequestLookupStore : IPullRequestLookupStore
    {
        public Task<PullRequestLookup?> FindAsync(
            string organizationId,
            string repositoryId,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<PullRequestLookup?>(
                new()
                {
                    OrganizationId = organizationId,
                    RepositoryId = repositoryId,
                    OwnerQualifiedRepoName = "zeeq-ai/zeeq",
                    PullRequestNumber = pullRequestNumber,
                    PullRequestRecordId = "pr_123",
                    PullRequestCreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
                    UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                }
            );

        public Task<PullRequestLookup> UpsertAsync(
            PullRequestLookup lookup,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class TestCodeReviewRecordStore : ICodeReviewRecordStore
    {
        public Task<IReadOnlyList<CodeReviewRecord>> ListForTriageAsync(
            string organizationId,
            FindingTriageTarget target,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CodeReviewRecord>>([]);

        public string? LastPullRequestRecordId { get; private set; }

        public string? LastBranch { get; private set; }

        public CodeReviewRecord Review { get; } =
            new()
            {
                Id = "cr_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                PullRequestRecordId = "pr_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "zeeq-ai/zeeq",
                PullRequestNumber = 42,
                Branch = "feature/review-findings",
                Title = "Add review findings",
                AuthorLogin = "octocat",
                Status = CodeReviewStatus.Completed,
                RequestOrigin = CodeReviewRequestOrigin.Manual,
                CriticalFindings = 1,
                MajorFindings = 1,
                MinorFindings = 1,
                FindingsStorageUri = "postgres://code-review-findings/org_123/cr_123/findings.xml",
                RemainingReviewBudget = 10,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

        public Task<CodeReviewRecord> AddAsync(
            CodeReviewRecord review,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeReviewRecord> UpdateAsync(
            CodeReviewRecord review,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeReviewRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeReviewRecord?> FindNewestForPullRequestAsync(
            string organizationId,
            string pullRequestRecordId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeReviewRecord?> FindNewestCompletedForPullRequestAsync(
            string organizationId,
            string pullRequestRecordId,
            DateTimeOffset pullRequestCreatedAtUtc,
            CancellationToken cancellationToken
        )
        {
            LastPullRequestRecordId = pullRequestRecordId;

            return Task.FromResult<CodeReviewRecord?>(Review);
        }

        public Task<CodeReviewRecord?> FindNewestCompletedForBranchAsync(
            string organizationId,
            string repositoryId,
            string branch,
            CancellationToken cancellationToken
        )
        {
            LastBranch = branch;

            return Task.FromResult<CodeReviewRecord?>(Review);
        }

        public Task<CodeReviewStreamPage<CodeReviewRecord>> ListRecentAsync(
            CodeReviewStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeReviewStreamPage<CodeReviewRecord>> ListForPullRequestAsync(
            PullRequestReviewStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<CodeReviewUpdateStreamPage> ListInboxUpdatesAsync(
            CodeReviewUpdateStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<CodeReviewRecord>> ListForAgentAsync(
            string organizationId,
            string? agentSessionId,
            string? reviewGroupId,
            int maxRecords,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class TestCodeReviewArtifactStore : ICodeReviewArtifactStore
    {
        public int OpenCount { get; private set; }

        public string StoredXml { get; set; } = string.Empty;

        public Task<string> WriteFindingsAsync(
            CodeReviewRecord review,
            Stream findings,
            string contentType,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<Stream> OpenFindingsAsync(
            string findingsStorageUri,
            CancellationToken cancellationToken
        )
        {
            OpenCount += 1;

            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(StoredXml)));
        }

        public Task CopyFindingsToAsync(
            string findingsStorageUri,
            Stream destination,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private static string ReviewXml() =>
        """
            <reviews noAgentsActivated="false">
              <review facet="Security" agent="Security Reviewer">
                <summary>Security summary</summary>
                <details>Security details</details>
                <findings>
                  <finding level="CRITICAL" file="src/Auth.cs" line="12" side="RIGHT">
                    <summary>Critical issue</summary>
                    <details>Critical body</details>
                  </finding>
                  <finding level="MAJOR" file="src/Data.cs" line="24" side="RIGHT">
                    <summary>Major issue</summary>
                    <details>Major body</details>
                  </finding>
                  <finding level="MINOR" file="src/Style.cs" line="36" side="RIGHT">
                    <summary>Minor issue</summary>
                    <details>Minor body</details>
                  </finding>
                </findings>
              </review>
            </reviews>
            """;
}
