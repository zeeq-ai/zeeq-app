using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for reviewer-agent test-run endpoints.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewAgentTestRunEndpointHandlerTests/*"
/// </summary>
public sealed class CodeReviewAgentTestRunEndpointHandlerTests
{
    [Test]
    public async Task ListCodeReviewAgentTestTargets_WithAdmin_ListsRepositoryPullRequestsInAnyState()
    {
        var fixture = Fixture.Create(role: "admin");
        fixture.PullRequests.Page = new(
            [
                fixture.CreatePullRequest("pr_123", 42, PullRequestState.Closed, isDraft: true),
                fixture.CreatePullRequest("pr_456", 43, PullRequestState.Open, isDraft: false),
            ],
            NextCursor: null,
            NewestCursor: new(fixture.PullRequest.CreatedAtUtc, fixture.PullRequest.Id)
        );

        var result = await fixture.ListTargetsHandler.HandleAsync(
            "org_123",
            "repo_123",
            cursorCreatedAtUtc: null,
            cursorId: null,
            pageSize: null,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewAgentTestTargetListResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Items).Count().IsEqualTo(2);
        await Assert.That(ok.Value.Items[0].State).IsEqualTo(PullRequestState.Closed);
        await Assert.That(ok.Value.Items[0].IsDraft).IsTrue();
        await Assert.That(fixture.PullRequests.LastQuery).IsNotNull();
        await Assert.That(fixture.PullRequests.LastQuery!.RepositoryId).IsEqualTo("repo_123");
        await Assert.That(fixture.PullRequests.LastQuery.ClaimStatus).IsNull();
        await Assert.That(fixture.PullRequests.LastQuery.SubjectUserId).IsNull();
    }

    [Test]
    public async Task ListCodeReviewAgentTestTargets_WithMember_ReturnsForbid()
    {
        var fixture = Fixture.Create(role: "member");

        var result = await fixture.ListTargetsHandler.HandleAsync(
            "org_123",
            "repo_123",
            cursorCreatedAtUtc: null,
            cursorId: null,
            pageSize: null,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
        await Assert.That(fixture.PullRequests.LastQuery).IsNull();
    }

    [Test]
    public async Task ListCodeReviewAgentTestTargets_WhenStoreReturnsMismatchedRepository_ReturnsNotFound()
    {
        var fixture = Fixture.Create(role: "admin");
        fixture.Repositories.EnforceOrganizationLookup = false;
        fixture.Repositories.Repository = fixture.CreateRepository(
            id: "repo_123",
            organizationId: "org_other"
        );

        var result = await fixture.ListTargetsHandler.HandleAsync(
            "org_123",
            "repo_123",
            cursorCreatedAtUtc: null,
            cursorId: null,
            pageSize: null,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
        await Assert.That(fixture.PullRequests.LastQuery).IsNull();
    }

    [Test]
    public async Task RunCodeReviewAgentTest_WithAdmin_ReturnsEphemeralFindingsFromDraftAgent()
    {
        var fixture = Fixture.Create(role: "owner");
        fixture.AgentExecutor.Xml = ReviewXml();

        var result = await fixture.RunTestHandler.HandleAsync(
            "org_123",
            "repo_123",
            fixture.Request(includePattern: ".cs"),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewAgentTestRunResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert
            .That(ok!.Value!.ResultKind)
            .IsEqualTo(CodeReviewAgentTestRunResultKind.Completed);
        await Assert.That(ok.Value.Review.Id).StartsWith("synthetic_");
        await Assert.That(ok.Value.Review.RequestOrigin).IsEqualTo(CodeReviewRequestOrigin.Manual);
        await Assert.That(ok.Value.Review.Status).IsEqualTo(CodeReviewStatus.Completed);
        await Assert.That(ok.Value.Review.MajorFindings).IsEqualTo(1);
        await Assert.That(ok.Value.Findings.Reviews).HasSingleItem();
        await Assert.That(ok.Value.Findings.Reviews.Single().Facet).IsEqualTo("Draft");
        await Assert.That(fixture.AgentExecutor.Options).IsEqualTo(CodeReviewExecutionOptions.Test);
        await Assert.That(fixture.AgentExecutor.ActiveReviewers).HasSingleItem();
        await Assert
            .That(fixture.AgentExecutor.ActiveReviewers.Single().Id)
            .IsEqualTo("draft-agent");
        await Assert
            .That(fixture.AgentExecutor.ActiveReviewers.Single().DisplayName)
            .IsEqualTo("Draft reviewer");
        await Assert.That(fixture.AgentExecutor.PreviousReviews).IsEmpty();
    }

    [Test]
    public async Task RunCodeReviewAgentTest_WithNullRequest_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "owner");

        var result = await fixture.RunTestHandler.HandleAsync(
            "org_123",
            "repo_123",
            null,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_agent_configuration");
        await Assert.That(badRequest.Value.Message).IsEqualTo("Request body is required.");
        await Assert.That(fixture.AgentExecutor.ExecuteCount).IsEqualTo(0);
    }

    [Test]
    public async Task RunCodeReviewAgentTest_WhenRepositoryFiltersExcludeAllFiles_ReturnsNoFilesState()
    {
        var fixture = Fixture.Create(role: "admin");
        fixture.Repository.ReviewConfiguration = new()
        {
            FileFilter = new()
            {
                IncludedFiles =
                [
                    new() { MatchType = CodeReviewFileNameMatchType.Extension, Pattern = ".ts" },
                ],
            },
        };

        var result = await fixture.RunTestHandler.HandleAsync(
            "org_123",
            "repo_123",
            fixture.Request(includePattern: ".cs"),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewAgentTestRunResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert
            .That(ok!.Value!.ResultKind)
            .IsEqualTo(CodeReviewAgentTestRunResultKind.NoFilesInScope);
        await Assert.That(ok.Value.InScopeFileCount).IsEqualTo(0);
        await Assert.That(ok.Value.ReviewerCount).IsEqualTo(0);
        await Assert.That(fixture.AgentExecutor.NoAgentsActivated).IsTrue();
    }

    [Test]
    public async Task RunCodeReviewAgentTest_WhenDraftAgentDoesNotActivate_ReturnsNoActivationState()
    {
        var fixture = Fixture.Create(role: "admin");

        var result = await fixture.RunTestHandler.HandleAsync(
            "org_123",
            "repo_123",
            fixture.Request(includePattern: ".ts"),
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewAgentTestRunResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert
            .That(ok!.Value!.ResultKind)
            .IsEqualTo(CodeReviewAgentTestRunResultKind.NoAgentActivation);
        await Assert.That(ok.Value.InScopeFileCount).IsEqualTo(1);
        await Assert.That(ok.Value.ReviewerCount).IsEqualTo(0);
        await Assert.That(fixture.AgentExecutor.NoAgentsActivated).IsTrue();
    }

    [Test]
    public async Task RunCodeReviewAgentTest_WithInvalidDraftAgent_ReturnsBadRequest()
    {
        var fixture = Fixture.Create(role: "owner");

        var result = await fixture.RunTestHandler.HandleAsync(
            "org_123",
            "repo_123",
            fixture.Request(displayName: " "),
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("invalid_agent_configuration");
        await Assert.That(fixture.AgentExecutor.ExecuteCount).IsEqualTo(0);
    }

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, "usr_123")],
                authenticationType: "test"
            )
        );

    private static string ReviewXml() =>
        """
            <reviews noAgentsActivated="false">
              <review facet="Draft" agent="Draft reviewer">
                <summary>Draft summary</summary>
                <details>Draft details</details>
                <findings>
                  <finding level="MAJOR" summary="Major" file="src/App.cs" line="10" side="RIGHT"><![CDATA[Major body]]></finding>
                </findings>
              </review>
            </reviews>
            """;

    private sealed class Fixture
    {
        private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow.AddMinutes(-20);

        private Fixture()
        {
            Repository = new()
            {
                Id = "repo_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                Provider = "github",
                OwnerQualifiedName = "owner/repo",
                DisplayName = "owner/repo",
                Enabled = true,
                CreatedAtUtc = _createdAt,
                UpdatedAtUtc = _createdAt,
            };
            PullRequest = new()
            {
                Id = "pr_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "owner/repo",
                PullRequestNumber = 42,
                GitHubNodeId = "PR_kw123",
                Title = "Test PR",
                Branch = "feature/test",
                BaseBranch = "main",
                HeadSha = "abc123",
                AuthorLogin = "octocat",
                HtmlUrl = "https://github.test/owner/repo/pull/42",
                State = PullRequestState.Open,
                ClaimStatus = PullRequestClaimStatus.Unclaimed,
                CreatedFromWebhookAtUtc = _createdAt,
                LastWebhookAtUtc = _createdAt,
                CreatedAtUtc = _createdAt,
                UpdatedAtUtc = _createdAt,
            };
        }

        public CodeRepository Repository { get; }
        public PullRequestRecord PullRequest { get; }
        public TestCodeRepositoryStore Repositories { get; } = new();
        public TestPullRequestRecordStore PullRequests { get; } = new();
        public TestCodeReviewPullRequestSource PullRequestSource { get; } = new();
        public TestCodeReviewAgentExecutor AgentExecutor { get; } = new();
        public ListCodeReviewAgentTestTargetsHandler ListTargetsHandler { get; private set; } =
            null!;
        public RunCodeReviewAgentTestHandler RunTestHandler { get; private set; } = null!;

        public static Fixture Create(string role)
        {
            var fixture = new Fixture();
            var memberships = Substitute.For<IZeeqMembershipStore>();
            memberships
                .ListActiveMembershipsForUserAsync("usr_123", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                        new()
                        {
                            Id = "mem_123",
                            OrganizationId = "org_123",
                            UserId = "usr_123",
                            Role = role,
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "usr_123",
                        },
                    ])
                );

            fixture.Repositories.Repository = fixture.Repository;
            fixture.PullRequests.Record = fixture.PullRequest;
            fixture.PullRequests.Page = new([fixture.PullRequest], null, null);
            fixture.PullRequestSource.Snapshot = new(
                "Live title",
                "Live body",
                [
                    new(
                        "src/App.cs",
                        null,
                        CodeReviewFileMutationState.Modified,
                        "@@ -1 +1\n+var value = 1;"
                    ),
                ],
                []
            );
            fixture.AgentExecutor.Xml = ReviewXml();

            var authorization = new CodeReviewAuthorization(memberships);
            fixture.ListTargetsHandler = new(
                authorization,
                fixture.Repositories,
                fixture.PullRequests
            );
            fixture.RunTestHandler = new(
                authorization,
                fixture.Repositories,
                fixture.PullRequests,
                fixture.CreateExecutionEngine()
            );

            return fixture;
        }

        public RunCodeReviewAgentTestRequest Request(
            string displayName = " Draft reviewer ",
            string includePattern = ".cs"
        ) =>
            new(
                PullRequest.Id,
                PullRequest.CreatedAtUtc,
                new(
                    displayName,
                    " Draft ",
                    CodeReviewModelTier.High,
                    " Review the draft behavior. ",
                    false,
                    new([new(CodeReviewFileNameMatchType.Extension, includePattern)], [])
                )
            );

        public CodeRepository CreateRepository(string id, string organizationId) =>
            new()
            {
                Id = id,
                OrganizationId = organizationId,
                TeamId = Repository.TeamId,
                Provider = Repository.Provider,
                OwnerQualifiedName = Repository.OwnerQualifiedName,
                DisplayName = Repository.DisplayName,
                Enabled = Repository.Enabled,
                CreatedAtUtc = Repository.CreatedAtUtc,
                UpdatedAtUtc = Repository.UpdatedAtUtc,
            };

        public PullRequestRecord CreatePullRequest(
            string id,
            int number,
            PullRequestState state,
            bool isDraft
        ) =>
            new()
            {
                Id = id,
                OrganizationId = PullRequest.OrganizationId,
                TeamId = PullRequest.TeamId,
                RepositoryId = PullRequest.RepositoryId,
                OwnerQualifiedRepoName = PullRequest.OwnerQualifiedRepoName,
                PullRequestNumber = number,
                GitHubNodeId = PullRequest.GitHubNodeId,
                Title = PullRequest.Title,
                Branch = PullRequest.Branch,
                BaseBranch = PullRequest.BaseBranch,
                HeadSha = PullRequest.HeadSha,
                AuthorLogin = PullRequest.AuthorLogin,
                HtmlUrl = PullRequest.HtmlUrl,
                IsDraft = isDraft,
                State = state,
                ClaimStatus = PullRequest.ClaimStatus,
                CreatedFromWebhookAtUtc = PullRequest.CreatedFromWebhookAtUtc,
                LastWebhookAtUtc = PullRequest.LastWebhookAtUtc,
                CreatedAtUtc = PullRequest.CreatedAtUtc,
                UpdatedAtUtc = PullRequest.UpdatedAtUtc,
            };

        private CodeReviewExecutionEngine CreateExecutionEngine()
        {
            var agentStore = Substitute.For<ICodeReviewerAgentStore>();
            var libraries = Substitute.For<ILibraryDocumentStore>();
            libraries
                .ListLibrariesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Library>>([]));

            return new(
                PullRequestSource,
                Repositories,
                PullRequests,
                new(agentStore, NullLogger<CodeReviewerAgentResolver>.Instance),
                AgentExecutor,
                Substitute.For<ICodeReviewPreviousReviewStore>(),
                new(),
                libraries,
                new TestHybridCache(),
                NullLogger<CodeReviewExecutionEngine>.Instance
            );
        }
    }

    private sealed class TestCodeReviewPullRequestSource : ICodeReviewPullRequestSource
    {
        public CodeReviewPullRequestSnapshot Snapshot { get; set; } = new("", "", [], []);

        public Task<CodeReviewPullRequestSnapshot> GetPullRequestAsync(
            CodeReviewRunRequested message,
            CancellationToken cancellationToken
        ) => Task.FromResult(Snapshot);
    }

    private sealed class TestCodeReviewAgentExecutor : ICodeReviewAgentExecutor
    {
        public IReadOnlyList<CodeReviewerRuntimeAgent> ActiveReviewers { get; private set; } = [];
        public IReadOnlyList<CodeReviewPreviousReview> PreviousReviews { get; private set; } = [];
        public CodeReviewExecutionOptions? Options { get; private set; }
        public bool NoAgentsActivated { get; private set; }
        public int ExecuteCount { get; private set; }
        public string Xml { get; set; } = ReviewXml();

        public Task<string> ExecuteAsync(
            string organizationId,
            IReadOnlyList<CodeReviewerRuntimeAgent> activeReviewers,
            bool noAgentsActivated,
            CodeReviewUserPrompt codeReviewUserPrompt,
            IReadOnlyList<CodeReviewPreviousReview> previousReviews,
            ClaimsPrincipal callerIdentity,
            CodeReviewTelemetryContext telemetry,
            CodeReviewExecutionOptions options,
            CancellationToken cancellationToken
        )
        {
            ExecuteCount++;
            ActiveReviewers = activeReviewers;
            PreviousReviews = previousReviews;
            Options = options;
            NoAgentsActivated = noAgentsActivated;

            return Task.FromResult(
                noAgentsActivated
                    ? CodeReviewXmlOutputValidator.Serialize(new() { NoAgentsActivated = true })
                    : Xml
            );
        }
    }

    private sealed class TestCodeRepositoryStore : ICodeRepositoryStore
    {
        public CodeRepository Repository { get; set; } = null!;
        public bool EnforceOrganizationLookup { get; set; } = true;

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => Task.FromResult<CodeRepository?>(Repository);

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
        ) => Task.FromResult<CodeRepository?>(Repository);

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CodeRepository>>([Repository]);

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<CodeRepository>>([Repository]);

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                !EnforceOrganizationLookup
                    ? Repository
                    : Repository.OrganizationId == organizationId && Repository.Id == repositoryId
                        ? Repository
                        : null
            );

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        ) => Task.FromResult(repository);

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);
    }

    private sealed class TestPullRequestRecordStore : IPullRequestRecordStore
    {
        public PullRequestRecord Record { get; set; } = null!;
        public CodeReviewStreamPage<PullRequestRecord> Page { get; set; } = new([], null, null);
        public PullRequestStreamQuery? LastQuery { get; private set; }

        public Task<PullRequestRecord> UpsertAsync(
            PullRequestRecord pullRequest,
            CancellationToken cancellationToken
        ) => Task.FromResult(pullRequest);

        public Task<PullRequestRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(Record.Id == id && Record.CreatedAtUtc == createdAtUtc ? Record : null);

        public Task<PullRequestRecord?> FindByHeadShaAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) => Task.FromResult<PullRequestRecord?>(null);

        public Task<PullRequestRecord?> FindByHeadShaWithCheckRunAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) => Task.FromResult<PullRequestRecord?>(null);

        public Task<IReadOnlyList<PullRequestRecord>> FindByNumberAsync(
            string organizationId,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<PullRequestRecord>>([]);

        public Task<CodeReviewStreamPage<PullRequestRecord>> ListRecentAsync(
            PullRequestStreamQuery query,
            CancellationToken cancellationToken
        )
        {
            LastQuery = query;

            return Task.FromResult(Page);
        }
    }
}
