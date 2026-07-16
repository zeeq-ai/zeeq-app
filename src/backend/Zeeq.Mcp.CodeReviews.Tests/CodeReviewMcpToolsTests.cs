using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Platform.CodeReviews;
using Danom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

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
        await Assert.That(response).Contains("uploadToken: token_123");
        await Assert.That(response).Contains("maxDiffSizeBytes: 1234");
        await Assert.That(response).Contains("curl -X PUT --data-binary");
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
        await Assert
            .That(runner.RunRequest!.Branch)
            .IsEqualTo("feat/branch-first-linking");
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
}
