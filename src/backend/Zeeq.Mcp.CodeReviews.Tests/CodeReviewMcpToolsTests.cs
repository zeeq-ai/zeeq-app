using System.Security.Claims;
using System.Text;
using Danom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Mcp.CodeReviews.Tests;

public sealed class CodeReviewMcpToolsTests
{
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
    public async Task GetReviewFindings_WithPullRequestAndMinimumMajor_ReturnsCriticalAndMajorFindings()
    {
        var fixture = ReviewFindingsFixture.Create();

        var response = await CodeReviewMcpTools.GetReviewFindings(
            fixture.Repositories,
            fixture.PullRequestLookups,
            fixture.Reviews,
            fixture.Artifacts,
            new CodeReviewXmlOutputValidator(),
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

        public Task<Result<ExpertCodeReviewRunResponse, string>> RunReviewAsync(
            ExpertCodeReviewRunRequest request,
            ClaimsPrincipal user,
            CancellationToken cancellationToken = default
        )
        {
            RunRequest = request;
            RunRequestUser = user;

            return Task.FromResult(
                Result<ExpertCodeReviewRunResponse, string>.Ok(
                    new(
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
