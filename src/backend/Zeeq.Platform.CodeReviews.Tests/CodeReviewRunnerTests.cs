using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests the provider-neutral code-review runner orchestration.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewRunnerTests/*"
/// </summary>
public sealed class CodeReviewRunnerTests
{
    [Test]
    public async Task RunAsync_WithValidReview_WritesArtifactAndReturnsCounts()
    {
        var fixture = RunnerFixture.Create();
        fixture.Repositories.Repository.ReviewConfiguration = new()
        {
            FileFilter = new()
            {
                IncludedFiles =
                [
                    new() { MatchType = CodeReviewFileNameMatchType.Extension, Pattern = ".cs" },
                ],
            },
        };
        fixture.AgentStore.Agents =
        [
            Agent(
                "agent_backend",
                "Backend",
                activation: new()
                {
                    IncludedFiles =
                    [
                        new()
                        {
                            MatchType = CodeReviewFileNameMatchType.Extension,
                            Pattern = ".cs",
                        },
                    ],
                }
            ),
        ];
        fixture.AgentExecutor.Xml = ReviewXml();

        var result = await fixture.Runner.RunAsync(
            fixture.Message,
            fixture.Review,
            CancellationToken.None
        );

        await Assert
            .That(result.SourceTelemetryPayload)
            .IsEqualTo(CodeReviewRecord.EmptySourceTelemetryPayload);
        await Assert.That(result.FindingsStorageUri).IsEqualTo(fixture.Artifacts.StorageUri);
        await Assert.That(result.CriticalFindings).IsEqualTo(1);
        await Assert.That(result.MajorFindings).IsEqualTo(1);
        await Assert.That(result.MinorFindings).IsEqualTo(1);
        await Assert.That(result.SuggestionFindings).IsEqualTo(1);
        await Assert.That(result.CommentFindings).IsEqualTo(1);
        await Assert.That(fixture.Artifacts.ContentType).IsEqualTo("application/xml");
        await Assert.That(fixture.Artifacts.StoredXml).IsEqualTo(fixture.AgentExecutor.Xml);
        await Assert
            .That(fixture.AgentExecutor.ActiveReviewers.Single().Id)
            .IsEqualTo("agent_backend");
        await Assert.That(fixture.AgentExecutor.NoAgentsActivated).IsFalse();
        await Assert.That(fixture.AgentExecutor.Prompt).Contains("<file_patch name=\"src/App.cs\"");
        await Assert.That(fixture.AgentExecutor.Prompt).Contains("<file name=\"docs/readme.md\"");
    }

    [Test]
    public async Task RunAsync_WithConfiguredAgentsButNoActivation_WritesNoAgentsArtifact()
    {
        var fixture = RunnerFixture.Create();
        fixture.AgentStore.Agents =
        [
            Agent(
                "agent_typescript",
                "TypeScript",
                activation: new()
                {
                    IncludedFiles =
                    [
                        new()
                        {
                            MatchType = CodeReviewFileNameMatchType.Extension,
                            Pattern = ".ts",
                        },
                    ],
                }
            ),
        ];
        fixture.AgentExecutor.Xml = CodeReviewXmlOutputValidator.Serialize(
            new() { NoAgentsActivated = true }
        );

        var result = await fixture.Runner.RunAsync(
            fixture.Message,
            fixture.Review,
            CancellationToken.None
        );

        await Assert
            .That(result.SourceTelemetryPayload)
            .IsEqualTo(CodeReviewRecord.EmptySourceTelemetryPayload);
        await Assert.That(result.FindingsStorageUri).IsEqualTo(fixture.Artifacts.StorageUri);
        await Assert.That(result.CriticalFindings).IsEqualTo(0);
        await Assert.That(result.MajorFindings).IsEqualTo(0);
        await Assert.That(result.MinorFindings).IsEqualTo(0);
        await Assert.That(result.SuggestionFindings).IsEqualTo(0);
        await Assert.That(result.CommentFindings).IsEqualTo(0);
        await Assert.That(fixture.AgentExecutor.ActiveReviewers).IsEmpty();
        await Assert.That(fixture.AgentExecutor.NoAgentsActivated).IsTrue();
    }

    [Test]
    public async Task RunAsync_WithInvalidExecutorXml_DoesNotWriteArtifact()
    {
        var fixture = RunnerFixture.Create();
        fixture.AgentExecutor.Xml = "<reviews>";

        Task Act() =>
            fixture.Runner.RunAsync(fixture.Message, fixture.Review, CancellationToken.None);

        await Assert
            .That(Act)
            .Throws<InvalidOperationException>()
            .WithMessageContaining("Code-review runner produced invalid XML:");
        await Assert.That(fixture.Artifacts.WriteCount).IsEqualTo(0);
    }

    private static CodeReviewerAgent Agent(
        string id,
        string facet,
        CodeReviewerActivationConfiguration? activation = null
    ) =>
        new()
        {
            Id = id,
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            DisplayName = facet + " Reviewer",
            ReviewFacet = facet,
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review " + facet + " concerns.",
            Enabled = true,
            ActivationConfiguration = activation ?? CodeReviewerActivationConfiguration.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static string ReviewXml() =>
        """
            <reviews noAgentsActivated="false">
              <review facet="Backend" agent="Backend Reviewer">
                <summary>Backend summary</summary>
                <details>Backend details</details>
                <findings>
                  <finding level="CRITICAL" summary="Critical" file="src/App.cs" line="10" side="RIGHT"><![CDATA[Critical body]]></finding>
                  <finding level="MAJOR" summary="Major" file="src/App.cs" line="11" side="RIGHT"><![CDATA[Major body]]></finding>
                  <finding level="MINOR" summary="Minor" file="src/App.cs" line="12" side="RIGHT"><![CDATA[Minor body]]></finding>
                  <finding level="SUGGESTION" summary="Suggestion" file="src/App.cs" line="13" side="RIGHT"><![CDATA[Suggestion body]]></finding>
                  <finding level="COMMENT" summary="Comment" file="src/App.cs" line="14" side="RIGHT"><![CDATA[Comment body]]></finding>
                </findings>
              </review>
            </reviews>
            """;

    [Test]
    public async Task RunAsync_RepositoryWithLibraryIds_IncludesLibraryNamesInPromptBody()
    {
        var fixture = RunnerFixture.Create();
        fixture.Libraries.Libraries =
        [
            new()
            {
                Id = "lib_123",
                OrganizationId = "org_123",
                Name = "test-library",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        ];
        fixture.Repositories.Repository.LibraryIds = ["lib_123"];

        await fixture.Runner.RunAsync(fixture.Message, fixture.Review, CancellationToken.None);

        await Assert.That(fixture.AgentExecutor.Prompt).Contains("test-library");
    }

    [Test]
    public async Task RunAsync_RepositoryWithNoLibraryIds_IncludesEmptyLibraryNamesInPromptBody()
    {
        var fixture = RunnerFixture.Create();
        fixture.Repositories.Repository.LibraryIds = [];

        await fixture.Runner.RunAsync(fixture.Message, fixture.Review, CancellationToken.None);

        await Assert.That(fixture.AgentExecutor.Prompt).Contains("<library_names />");
    }

    [Test]
    public async Task RunAsync_BuildsAutomationIdentityScopedToRepositoryOrgAndTeam()
    {
        var fixture = RunnerFixture.Create();
        // The fixture already sets OrganizationId="org_123" and TeamId="team_123"
        // on both the repository and message, so the identity should match.

        await fixture.Runner.RunAsync(fixture.Message, fixture.Review, CancellationToken.None);

        var identity = fixture.AgentExecutor.CallerIdentity;
        await Assert.That(identity).IsNotNull();
        await Assert.That(identity.FindFirstValue("org_id")).IsEqualTo("org_123");
        await Assert.That(identity.FindFirstValue("team_id")).IsEqualTo("team_123");
        await Assert.That(identity.FindFirstValue("sub")).IsEqualTo("system:code-review-agent");
    }

    private sealed class RunnerFixture
    {
        private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;

        private RunnerFixture()
        {
            Review = new()
            {
                Id = "review_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                PullRequestRecordId = "pr_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "owner/repo",
                PullRequestNumber = 42,
                Branch = "feature/test",
                Title = "Test PR",
                AuthorLogin = "octocat",
                Status = CodeReviewStatus.Running,
                RequestOrigin = CodeReviewRequestOrigin.RepositoryWebhook,
                RemainingReviewBudget = 2,
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
            Message = new()
            {
                OrganizationId = "org_123",
                TeamId = "team_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "owner/repo",
                PullRequestNumber = 42,
                PullRequestRecordId = PullRequest.Id,
                PullRequestCreatedAtUtc = PullRequest.CreatedAtUtc,
                CodeReviewRecordId = Review.Id,
                CodeReviewCreatedAtUtc = Review.CreatedAtUtc,
                GitHubDeliveryId = "delivery_123",
                TraceContext = new ZeeqTraceContext(null, null),
            };
        }

        public TestCodeRepositoryStore Repositories { get; } = new();
        public TestLibraryDocumentStore Libraries { get; } = new();
        public TestPullRequestRecordStore PullRequests { get; } = new();
        public TestCodeReviewPullRequestSource PullRequestSource { get; } = new();
        public TestCodeReviewerAgentStore AgentStore { get; } = new();
        public TestCodeReviewAgentExecutor AgentExecutor { get; } = new();
        public TestCodeReviewArtifactStore Artifacts { get; } = new();
        public CodeReviewRunner Runner { get; private set; } = null!;
        public CodeReviewRecord Review { get; }
        public PullRequestRecord PullRequest { get; }
        public CodeReviewRunRequested Message { get; }

        public static RunnerFixture Create()
        {
            var fixture = new RunnerFixture();
            fixture.Repositories.Repository = new()
            {
                Id = "repo_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                Provider = "github",
                OwnerQualifiedName = "owner/repo",
                DisplayName = "owner/repo",
                Enabled = true,
                CreatedAtUtc = fixture._createdAt,
                UpdatedAtUtc = fixture._createdAt,
            };
            fixture.PullRequests.Record = fixture.PullRequest;
            fixture.PullRequestSource.Snapshot = new(
                "Current title",
                "Current body",
                [
                    new(
                        "src/App.cs",
                        null,
                        CodeReviewFileMutationState.Modified,
                        "@@ -1 +1\n+var value = 1;"
                    ),
                    new(
                        "docs/readme.md",
                        null,
                        CodeReviewFileMutationState.Added,
                        "@@ -0 +1\n+# Docs"
                    ),
                ],
                []
            );
            fixture.AgentExecutor.Xml = ReviewXml();
            fixture.Runner = new(
                fixture.PullRequestSource,
                fixture.Repositories,
                fixture.PullRequests,
                new(fixture.AgentStore, NullLogger<CodeReviewerAgentResolver>.Instance),
                fixture.AgentExecutor,
                new TestCodeReviewPreviousReviewStore(),
                new(),
                fixture.Artifacts,
                fixture.Libraries,
                new TestHybridCache(),
                NullLogger<CodeReviewRunner>.Instance
            );

            return fixture;
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

    private sealed class TestCodeReviewArtifactStore : ICodeReviewArtifactStore
    {
        public string StorageUri { get; } =
            "postgres://code-review-findings/org_123/review_123/202606250000000000000.xml";

        public string StoredXml { get; private set; } = string.Empty;
        public string ContentType { get; private set; } = string.Empty;
        public int WriteCount { get; private set; }

        public async Task<string> WriteFindingsAsync(
            CodeReviewRecord review,
            Stream findings,
            string contentType,
            CancellationToken cancellationToken
        )
        {
            using var reader = new StreamReader(findings, Encoding.UTF8);
            StoredXml = await reader.ReadToEndAsync(cancellationToken);
            ContentType = contentType;
            WriteCount++;

            return StorageUri;
        }

        public Task<Stream> OpenFindingsAsync(
            string findingsStorageUri,
            CancellationToken cancellationToken
        ) => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(StoredXml)));

        public async Task CopyFindingsToAsync(
            string findingsStorageUri,
            Stream destination,
            CancellationToken cancellationToken
        )
        {
            await using var source = new MemoryStream(Encoding.UTF8.GetBytes(StoredXml));
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private sealed class TestCodeRepositoryStore : ICodeRepositoryStore
    {
        public CodeRepository Repository { get; set; } = null!;

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) => Task.FromResult<CodeRepository?>(Repository);

        public Task<CodeRepository?> FindActiveForOrganizationByProviderIdentityAsync(
            string organizationId,
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Repository.OrganizationId == organizationId
                && Repository.Provider == provider
                && Repository.OwnerQualifiedName == ownerQualifiedName
                && Repository.Enabled
                && Repository.DisabledAtUtc is null
                    ? Repository
                    : null
            );

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
                Repository.OrganizationId == organizationId && Repository.Id == repositoryId
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
        ) =>
            Task.FromResult(
                Record.OrganizationId == organizationId
                && Record.RepositoryId == repositoryId
                && Record.HeadSha == headSha
                    ? Record
                    : null
            );

        public Task<PullRequestRecord?> FindByHeadShaWithCheckRunAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Record.OrganizationId == organizationId
                && Record.RepositoryId == repositoryId
                && Record.HeadSha == headSha
                && Record.CheckRunState != null
                    ? Record
                    : null
            );

        public Task<IReadOnlyList<PullRequestRecord>> FindByNumberAsync(
            string organizationId,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<PullRequestRecord>>(
                Record.OrganizationId == organizationId
                && Record.PullRequestNumber == pullRequestNumber
                    ? [Record]
                    : []
            );

        public Task<CodeReviewStreamPage<PullRequestRecord>> ListRecentAsync(
            PullRequestStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class TestCodeReviewerAgentStore : ICodeReviewerAgentStore
    {
        public IReadOnlyList<CodeReviewerAgent> Agents { get; set; } = [];

        public Task<IReadOnlyList<CodeReviewerAgent>> ListForRepositoryAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([
                .. Agents.Where(agent =>
                    agent.OrganizationId == organizationId && agent.RepositoryId == repositoryId
                ),
            ]);

        public Task<IReadOnlyList<CodeReviewerAgent>> ListEnabledForRepositoryAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([
                .. Agents.Where(agent =>
                    agent.OrganizationId == organizationId
                    && agent.RepositoryId == repositoryId
                    && agent.Enabled
                ),
            ]);

        public Task<CodeReviewerAgent?> FindAsync(
            string organizationId,
            string agentId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Agents.FirstOrDefault(agent =>
                    agent.OrganizationId == organizationId && agent.Id == agentId
                )
            );

        public Task<CodeReviewerAgent> AddAsync(
            CodeReviewerAgent agent,
            CancellationToken cancellationToken
        ) => Task.FromResult(agent);

        public Task<CodeReviewerAgent> UpdateAsync(
            CodeReviewerAgent agent,
            CancellationToken cancellationToken
        ) => Task.FromResult(agent);

        public Task<bool> DisableAsync(
            string organizationId,
            string agentId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);
    }

    private sealed class TestCodeReviewPreviousReviewStore : ICodeReviewPreviousReviewStore
    {
        public IReadOnlyList<CodeReviewPreviousReview> PreviousReviews { get; set; } = [];

        public Task<IReadOnlyList<CodeReviewPreviousReview>> LoadAsync(
            string organizationId,
            string ownerQualifiedRepoName,
            int pullRequestNumber,
            string reviewGroupId,
            string excludeReviewId,
            int maxRecords = 3,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(PreviousReviews);

        public Task<IReadOnlyList<CodeReviewPreviousReview>> LoadForAgentAsync(
            string organizationId,
            string? agentSessionId,
            string? reviewGroupId,
            string excludeReviewId,
            int maxRecords = 3,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }

    private sealed class TestLibraryDocumentStore : ILibraryDocumentStore
    {
        public IReadOnlyList<Library> Libraries { get; set; } = [];

        public Task<Library?> GetLibraryAsync(
            string organizationId,
            string name,
            CancellationToken ct
        ) => Task.FromResult<Library?>(Libraries.FirstOrDefault(l => l.Name == name));

        public Task<IReadOnlyList<Library>> ListLibrariesAsync(
            string organizationId,
            CancellationToken ct
        ) => Task.FromResult(Libraries);

        public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
            string publicSourceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteLibraryAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library?> GetLibraryByIdAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
            int limit,
            TimeSpan staleAfter,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task SetProcessingStatusAsync(
            LibraryDocument document,
            DocumentProcessingStatus status,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Library> UpdateSyncStateAsync(
            string organizationId,
            string libraryId,
            string? syncStatus,
            DateTimeOffset? nextSyncAt,
            DateTimeOffset[] manualTriggerHistory,
            DateTimeOffset? sourceSyncedAt,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> DeleteUnstampedAsync(
            string organizationId,
            string libraryId,
            string currentSyncRunId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument> UpsertDocumentAsync(
            LibraryDocument document,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task DeleteDocumentAsync(
            string organizationId,
            string libraryId,
            string path,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
            string organizationId,
            string libraryId,
            string query,
            int limit,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> GetByPathAsync(
            string organizationId,
            string libraryId,
            string input,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
            string organizationId,
            string libraryId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> MoveDocumentAsync(
            string organizationId,
            string libraryId,
            string fromPath,
            string toPath,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
            string organizationId,
            string libraryId,
            string path,
            bool excluded,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }
}
