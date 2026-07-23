using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Common.Storage;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenIddict.Abstractions;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class ExpertCodeReviewRunnerTests
{
    [Test]
    public async Task CreateUploadUrlAsync_WithAuthenticatedUser_ReturnsTokenAndUploadUrl()
    {
        var fixture = Fixture.Create();

        var response = await fixture.Runner.CreateUploadUrlAsync(
            TestUser(),
            CancellationToken.None
        );
        var tokenValid = fixture.TokenProtector.TryUnprotect(response.UploadToken, out var payload);

        await Assert.That(response.JobId).IsNotEmpty();
        await Assert
            .That(response.UploadUrl)
            .StartsWith("http://api.test/api/v1/code-review/mcp-diffs/");
        await Assert.That(response.CurlExample).Contains("--data-binary");
        await Assert.That(tokenValid).IsTrue();
        await Assert.That(payload!.JobId).IsEqualTo(response.JobId);
        await Assert.That(payload.CreatedById).IsEqualTo("usr_123");
        await Assert.That(payload.OrganizationId).IsEqualTo("org_123");
    }

    [Test]
    public async Task RunReviewAsync_WithUnmappedRepository_UsesDefaultReviewerAndDeletesDiff()
    {
        var fixture = Fixture.Create();
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(Diff("src/App.cs")));
        fixture
            .Repositories.FindActiveAsync("github", "owner/repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeRepository?>(null));

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(),
            TestUser(),
            CancellationToken.None
        );
        var response = Unwrap(result);

        await Assert.That(response.ReviewXml).IsEqualTo(fixture.AgentExecutor.Xml);
        await Assert.That(response.ReviewedFiles).IsEquivalentTo(["src/App.cs"]);
        await Assert.That(response.OutOfScopeFiles).IsEmpty();
        await Assert.That(fixture.AgentExecutor.OrganizationId).IsEqualTo("org_123");
        await Assert
            .That(fixture.AgentExecutor.ActiveReviewers.Single().Id)
            .IsEqualTo(CodeReviewerAgentResolver.DefaultReviewerId);
        await Assert.That(fixture.AgentExecutor.Prompt).Contains("Local changes");
        await fixture
            .Storage.Received(1)
            .DeleteAsync(fixture.Path, StorageContainer.CodeReviewDiffs, CancellationToken.None);
    }

    [Test]
    public async Task RunReviewAsync_WithMappedRepository_AppliesFileFilterAndConfiguredAgents()
    {
        var fixture = Fixture.Create();
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(Diff("src/App.cs") + "\n" + Diff("docs/readme.md")));
        fixture
            .Repositories.FindActiveAsync("github", "owner/repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeRepository?>(Repository(includeExtension: ".cs")));
        fixture
            .AgentStore.ListEnabledForRepositoryAsync(
                "repo_org",
                "repo_123",
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([Agent()]));

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(),
            TestUser(),
            CancellationToken.None
        );
        var response = Unwrap(result);

        await Assert.That(response.ReviewedFiles).IsEquivalentTo(["src/App.cs"]);
        await Assert.That(response.OutOfScopeFiles).IsEquivalentTo(["docs/readme.md"]);
        await Assert.That(fixture.AgentExecutor.OrganizationId).IsEqualTo("repo_org");
        await Assert
            .That(fixture.AgentExecutor.ActiveReviewers.Single().Id)
            .IsEqualTo("agent_backend");
        await Assert.That(fixture.AgentExecutor.NoAgentsActivated).IsFalse();
    }

    [Test]
    public async Task RunReviewAsync_WithMappedRepositorySharedPromptFragment_IncludesOrganizationGuidance()
    {
        var fixture = Fixture.Create();
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(Diff("src/App.cs")));
        fixture
            .Repositories.FindActiveAsync("github", "owner/repo", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<CodeRepository?>(
                    Repository(
                        includeExtension: ".cs",
                        sharedPromptFragment: "Always flag missing null checks."
                    )
                )
            );
        fixture
            .AgentStore.ListEnabledForRepositoryAsync(
                "repo_org",
                "repo_123",
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([Agent()]));

        await fixture.Runner.RunReviewAsync(fixture.Request(), TestUser(), CancellationToken.None);

        await Assert
            .That(fixture.AgentExecutor.Prompt)
            .Contains("Always flag missing null checks.");
    }

    [Test]
    public async Task RunReviewAsync_WithConfiguredButInactiveAgents_ReturnsNoAgentsXml()
    {
        var fixture = Fixture.Create();
        fixture.AgentExecutor.Xml = CodeReviewXmlOutputValidator.Serialize(
            new() { NoAgentsActivated = true }
        );
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(Diff("src/App.cs")));
        fixture
            .Repositories.FindActiveAsync("github", "owner/repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeRepository?>(Repository(includeExtension: ".cs")));
        fixture
            .AgentStore.ListEnabledForRepositoryAsync(
                "repo_org",
                "repo_123",
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([
                    Agent(
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
                ])
            );

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(Unwrap(result).ReviewXml).Contains("noAgentsActivated=\"true\"");
        await Assert.That(fixture.AgentExecutor.ActiveReviewers).IsEmpty();
        await Assert.That(fixture.AgentExecutor.NoAgentsActivated).IsTrue();
    }

    [Test]
    public async Task RunReviewAsync_WithValidExplicitLibrary_ReplacesConfiguredLibraries()
    {
        var fixture = Fixture.Create();
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(Diff("src/App.cs")));
        fixture
            .Repositories.FindActiveAsync("github", "owner/repo", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<CodeRepository?>(
                    Repository(includeExtension: ".cs", libraryIds: ["lib_configured"])
                )
            );
        fixture
            .Libraries.ListLibrariesAsync("repo_org", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<Library>>([
                    LibraryModel("lib_configured", "repo_org", "configured-library"),
                    LibraryModel("lib_requested", "repo_org", "requested-library"),
                ])
            );

        await fixture.Runner.RunReviewAsync(
            fixture.Request(libraries: ["requested-library", "not-a-real-library"]),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(fixture.AgentExecutor.Prompt).Contains("requested-library");
        await Assert.That(fixture.AgentExecutor.Prompt).DoesNotContain("configured-library");
    }

    [Test]
    public async Task RunReviewAsync_WithNoValidExplicitLibraries_FallsBackToConfiguredLibraries()
    {
        var fixture = Fixture.Create();
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(Diff("src/App.cs")));
        fixture
            .Repositories.FindActiveAsync("github", "owner/repo", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<CodeRepository?>(
                    Repository(includeExtension: ".cs", libraryIds: ["lib_configured"])
                )
            );
        fixture
            .Libraries.ListLibrariesAsync("repo_org", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<Library>>([
                    LibraryModel("lib_configured", "repo_org", "configured-library"),
                ])
            );

        await fixture.Runner.RunReviewAsync(
            fixture.Request(libraries: ["not-a-real-library"]),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(fixture.AgentExecutor.Prompt).Contains("configured-library");
    }

    [Test]
    public async Task RunReviewAsync_WithInvalidToken_ReturnsError()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(uploadToken: "not-a-token"),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(UnwrapError(result)).Contains("Invalid or unrecognized review token");
    }

    [Test]
    public async Task RunReviewAsync_WithExpiredToken_ReturnsExpiredError()
    {
        var fixture = Fixture.Create(expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(UnwrapError(result)).Contains("token has expired");
    }

    [Test]
    public async Task RunReviewAsync_WithTokenJobMismatch_ReturnsError()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(jobId: "different-job"),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(UnwrapError(result)).Contains("does not match");
    }

    [Test]
    public async Task RunReviewAsync_WithMissingDiff_ReturnsErrorAndDeletesPath()
    {
        var fixture = Fixture.Create();
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromException<string>(new FileNotFoundException("missing", fixture.Path))
            );

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(UnwrapError(result)).Contains("No uploaded diff found");
        await fixture
            .Storage.Received(1)
            .DeleteAsync(fixture.Path, StorageContainer.CodeReviewDiffs, CancellationToken.None);
    }

    [Test]
    public async Task RunReviewAsync_WithUnparseableDiff_ReturnsError()
    {
        var fixture = Fixture.Create();
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult("not a diff"));

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(UnwrapError(result)).Contains("parsable file sections");
    }

    [Test]
    public async Task RunReviewAsync_WithInvalidXml_ReturnsErrorAndDeletesDiff()
    {
        var fixture = Fixture.Create();
        fixture.AgentExecutor.Xml = "<reviews>";
        fixture
            .Storage.ReadTextAsync(
                fixture.Path,
                StorageContainer.CodeReviewDiffs,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(Diff("src/App.cs")));
        fixture
            .Repositories.FindActiveAsync("github", "owner/repo", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeRepository?>(null));

        var result = await fixture.Runner.RunReviewAsync(
            fixture.Request(),
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(UnwrapError(result)).Contains("invalid XML");
        await fixture
            .Storage.Received(1)
            .DeleteAsync(fixture.Path, StorageContainer.CodeReviewDiffs, CancellationToken.None);
    }

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

    private static string Diff(string path) =>
        $$"""
            diff --git a/{{path}} b/{{path}}
            index 1111111..2222222 100644
            --- a/{{path}}
            +++ b/{{path}}
            @@ -1 +1 @@
            -old
            +new
            """;

    private static CodeRepository Repository(
        string includeExtension,
        string sharedPromptFragment = "",
        string[]? libraryIds = null
    ) =>
        new()
        {
            Id = "repo_123",
            OrganizationId = "repo_org",
            TeamId = null,
            Provider = "github",
            OwnerQualifiedName = "owner/repo",
            DisplayName = "owner/repo",
            Enabled = true,
            LibraryIds = libraryIds ?? [],
            ReviewConfiguration = new()
            {
                FileFilter = new()
                {
                    IncludedFiles =
                    [
                        new()
                        {
                            MatchType = CodeReviewFileNameMatchType.Extension,
                            Pattern = includeExtension,
                        },
                    ],
                },
                SharedPromptFragment = sharedPromptFragment,
            },
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static Library LibraryModel(string id, string organizationId, string name) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static CodeReviewerAgent Agent(
        CodeReviewerActivationConfiguration? activation = null
    ) =>
        new()
        {
            Id = "agent_backend",
            OrganizationId = "repo_org",
            RepositoryId = "repo_123",
            DisplayName = "Backend Reviewer",
            ReviewFacet = "Backend",
            ModelTier = CodeReviewModelTier.High,
            Prompt = "Review backend changes.",
            Enabled = true,
            ActivationConfiguration = activation ?? CodeReviewerActivationConfiguration.Empty,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static ExpertCodeReviewRunResponse Unwrap(
        Danom.Result<ExpertCodeReviewRunResponse, string> result
    ) => result.Match(ok => ok, error => throw new InvalidOperationException(error));

    private static string UnwrapError(Danom.Result<ExpertCodeReviewRunResponse, string> result) =>
        result.Match(_ => throw new InvalidOperationException("Expected error."), error => error);

    private static string ReviewXml() =>
        """
            <reviews noAgentsActivated="false">
              <review facet="General" agent="Principal Software Engineer">
                <summary>Looks good.</summary>
                <details>No issues.</details>
                <findings />
              </review>
            </reviews>
            """;

    private sealed class Fixture
    {
        private Fixture() { }

        public string JobId { get; } = "018ff6a5f6e57b06b4c1a0f9c13e0f12";

        public string Path => $"{JobId}/diff.txt";

        public string Token { get; private init; } = string.Empty;

        public CodeReviewDiffUploadTokenProtector TokenProtector { get; private init; } = null!;

        public IStorageProvider<PostgresStorageWriteOptions> Storage { get; private init; } = null!;

        public ICodeRepositoryStore Repositories { get; private init; } = null!;

        public ICodeReviewerAgentStore AgentStore { get; private init; } = null!;

        public ILibraryDocumentStore Libraries { get; private init; } = null!;

        public TestCodeReviewAgentExecutor AgentExecutor { get; private init; } = null!;

        public ExpertCodeReviewRunner Runner { get; private init; } = null!;

        public ExpertCodeReviewRunRequest Request(
            string? jobId = null,
            string? uploadToken = null,
            IReadOnlyList<string>? libraries = null
        ) =>
            new(
                jobId ?? JobId,
                uploadToken ?? Token,
                "owner/repo",
                "Local changes",
                "Review the local diff.",
                Branch: null,
                AgentSessionId: null,
                ReviewGroupId: null,
                Libraries: libraries
            );

        public static Fixture Create(DateTimeOffset? expiresAtUtc = null)
        {
            var appSettings = new AppSettings
            {
                Http = new HttpSettings
                {
                    ApiBaseUri = "http://api.test",
                    FrontendBaseUri = "http://frontend.test",
                },
                CodeReview = new CodeReviewSettings
                {
                    ReviewRequestLinkEncryptionKey = "test-key",
                    DiffUploadUrlValidityMinutes = 30,
                },
            };
            var tokenProtector = new CodeReviewDiffUploadTokenProtector(appSettings.CodeReview);
            var storage = Substitute.For<IStorageProvider<PostgresStorageWriteOptions>>();
            storage
                .DeleteAsync(
                    Arg.Any<string>(),
                    Arg.Any<StorageContainer>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult(true));
            var repositories = Substitute.For<ICodeRepositoryStore>();
            var agentStore = Substitute.For<ICodeReviewerAgentStore>();
            agentStore
                .ListEnabledForRepositoryAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult<IReadOnlyList<CodeReviewerAgent>>([]));
            var agentExecutor = new TestCodeReviewAgentExecutor { Xml = ReviewXml() };

            // Configure store substitutes so the runner can persist and link agent reviews.
            CodeReviewRecord? persistedReview = null;
            var codeReviewStore = Substitute.For<ICodeReviewRecordStore>();
            codeReviewStore
                .AddAsync(Arg.Any<CodeReviewRecord>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    persistedReview = callInfo.Arg<CodeReviewRecord>();
                    return Task.FromResult(persistedReview);
                });
            codeReviewStore
                .FindAsync(
                    Arg.Any<string>(),
                    Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(_ => Task.FromResult(persistedReview));
            codeReviewStore
                .UpdateAsync(Arg.Any<CodeReviewRecord>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(callInfo.Arg<CodeReviewRecord>()));
            var artifactStore = Substitute.For<ICodeReviewArtifactStore>();
            artifactStore
                .WriteFindingsAsync(
                    Arg.Any<CodeReviewRecord>(),
                    Arg.Any<Stream>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult("s3://test/findings.xml"));
            var previousReviewStore = Substitute.For<ICodeReviewPreviousReviewStore>();
            previousReviewStore
                .LoadForAgentAsync(
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult<IReadOnlyList<CodeReviewPreviousReview>>([]));
            var linkFactory = new CodeReviewRequestLinkFactory(
                Options.Create(
                    new AppSettings
                    {
                        Http = new HttpSettings { FrontendBaseUri = "http://frontend.test" },
                    }
                ),
                new CodeReviewRequestTokenProtector(
                    new CodeReviewSettings { ReviewRequestLinkEncryptionKey = "test-key" }
                )
            );

            var libraries = Substitute.For<ILibraryDocumentStore>();
            libraries
                .ListLibrariesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Library>>([]));

            var fixture = new Fixture
            {
                TokenProtector = tokenProtector,
                Storage = storage,
                Repositories = repositories,
                AgentStore = agentStore,
                Libraries = libraries,
                AgentExecutor = agentExecutor,
                Token = tokenProtector.Protect(
                    CodeReviewDiffUploadTokenProtector.CreatePayload(
                        "018ff6a5f6e57b06b4c1a0f9c13e0f12",
                        expiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(10),
                        "usr_123",
                        "org_123"
                    )
                ),
                Runner = new(
                    storage,
                    tokenProtector,
                    new GitDiffParser(),
                    repositories,
                    new CodeReviewerAgentResolver(
                        agentStore,
                        NullLogger<CodeReviewerAgentResolver>.Instance
                    ),
                    agentExecutor,
                    new CodeReviewXmlOutputValidator(),
                    libraries,
                    new TestHybridCache(),
                    Options.Create(appSettings),
                    codeReviewStore,
                    artifactStore,
                    previousReviewStore,
                    linkFactory,
                    NullLogger<ExpertCodeReviewRunner>.Instance
                ),
            };

            return fixture;
        }
    }
}
