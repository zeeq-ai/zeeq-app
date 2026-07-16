using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for loading parsed code-review findings.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewFindingsEndpointHandlerTests/*"
/// </summary>
public sealed class CodeReviewFindingsEndpointHandlerTests
{
    [Test]
    public async Task GetCodeReviewFindings_WithStoredFindings_ReturnsParsedReviewerFindings()
    {
        var fixture = Fixture.Create();
        fixture.CodeReviews.Record.CriticalFindings = 1;
        fixture.CodeReviews.Record.FindingsStorageUri = fixture.Artifacts.StorageUri;
        fixture.Artifacts.StoredXml = ReviewXml();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.CodeReviews.Record.Id,
            fixture.CodeReviews.Record.CreatedAtUtc,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewFindingsResponse>;
        var reviewer = ok!.Value!.Reviews.Single();
        var finding = reviewer.Findings.Single();

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok.Value.CodeReviewRecordId).IsEqualTo("cr_123");
        await Assert.That(ok.Value.NoAgentsActivated).IsFalse();
        await Assert.That(reviewer.Facet).IsEqualTo("Security");
        await Assert.That(reviewer.Agent).IsEqualTo("Security Reviewer");
        await Assert.That(reviewer.Summary).IsEqualTo("Security summary");
        await Assert.That(reviewer.Details).IsEqualTo("Security details");
        await Assert.That(finding.Level).IsEqualTo(CodeReviewFindingLevel.Critical);
        await Assert.That(finding.File).IsEqualTo("src/App.cs");
        await Assert.That(finding.Line).IsEqualTo(42);
        await Assert.That(finding.Side).IsEqualTo("RIGHT");
        await Assert.That(finding.Summary).IsEqualTo("Critical issue");
        await Assert.That(finding.Body).IsEqualTo("Critical body");
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetCodeReviewFindings_WithZeroFindings_DoesNotOpenArtifact()
    {
        var fixture = Fixture.Create();
        fixture.CodeReviews.Record.FindingsStorageUri = fixture.Artifacts.StorageUri;
        fixture.Artifacts.StoredXml = ReviewXml();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.CodeReviews.Record.Id,
            fixture.CodeReviews.Record.CreatedAtUtc,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewFindingsResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Reviews).IsEmpty();
        await Assert.That(ok.Value.NoAgentsActivated).IsFalse();
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetCodeReviewFindings_WithoutCreatedAt_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.CodeReviews.Record.Id,
            createdAtUtc: null,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_created_at");
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetCodeReviewFindings_WithTelemetry_ReturnsSourceTelemetry()
    {
        var fixture = Fixture.Create();
        fixture.CodeReviews.Record.CriticalFindings = 1;
        fixture.CodeReviews.Record.FindingsStorageUri = fixture.Artifacts.StorageUri;
        fixture.CodeReviews.Record.SourceTelemetryPayload = PopulatedTelemetryPayload();
        fixture.Artifacts.StoredXml = ReviewXml();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.CodeReviews.Record.Id,
            fixture.CodeReviews.Record.CreatedAtUtc,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewFindingsResponse>;
        var telemetry = ok!.Value!.SourceTelemetry;

        await Assert.That(telemetry).IsNotNull();
        await Assert.That(telemetry!.Summary.DocumentCount).IsEqualTo(1);
        var document = telemetry.Documents.Single();
        await Assert.That(document.Path).IsEqualTo("/backend/dotnet-csharp-best-practices.md");
        await Assert.That(document.ReadAfterSearch).IsTrue();
        await Assert.That(document.Snippets.Single().SnippetId).IsEqualTo("sn_a");
        await Assert.That(telemetry.ToolUsage.Single().Tool).IsEqualTo("search_sections");
    }

    [Test]
    public async Task GetCodeReviewFindings_ZeroFindingsWithTelemetry_ReturnsTelemetryAndEmptyReviews()
    {
        var fixture = Fixture.Create();
        // Zero findings, but telemetry was captured — the clean-PR "how we got here" case.
        fixture.CodeReviews.Record.SourceTelemetryPayload = PopulatedTelemetryPayload();
        fixture.Artifacts.StoredXml = ReviewXml();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.CodeReviews.Record.Id,
            fixture.CodeReviews.Record.CreatedAtUtc,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewFindingsResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Reviews).IsEmpty();
        await Assert.That(ok.Value.SourceTelemetry).IsNotNull();
        await Assert.That(ok.Value.SourceTelemetry!.Documents).HasSingleItem();
        // Telemetry must not require opening the findings artifact.
        await Assert.That(fixture.Artifacts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task ToDto_SetsHasSourceTelemetry_BasedOnPayload()
    {
        var fixture = Fixture.Create();

        fixture.CodeReviews.Record.SourceTelemetryPayload =
            CodeReviewRecord.EmptySourceTelemetryPayload;
        var withoutTelemetry = CodeReviewEndpointMapping.ToDto(fixture.CodeReviews.Record);

        fixture.CodeReviews.Record.SourceTelemetryPayload = PopulatedTelemetryPayload();
        var withTelemetry = CodeReviewEndpointMapping.ToDto(fixture.CodeReviews.Record);

        await Assert.That(withoutTelemetry.HasSourceTelemetry).IsFalse();
        await Assert.That(withTelemetry.HasSourceTelemetry).IsTrue();
    }

    private static string PopulatedTelemetryPayload() =>
        CodeReviewSourceTelemetrySerializer.Serialize(
            new CodeReviewSourceTelemetry(
                SchemaVersion: CodeReviewSourceTelemetry.CurrentSchemaVersion,
                Summary: new(
                    DocumentCount: 1,
                    SnippetCount: 1,
                    SourceHitCount: 3,
                    ToolCallCount: 2,
                    MissedQueryCount: 0
                ),
                Documents:
                [
                    new(
                        DocumentId: "doc_a",
                        Library: "zeeq-app",
                        Path: "/backend/dotnet-csharp-best-practices.md",
                        Title: "C# Guidelines",
                        HitCount: 3,
                        Usages: ["Read", "Searched"],
                        ReadAfterSearch: true,
                        Facets: ["Security"],
                        BestRank: 1,
                        BestScore: 0.0312,
                        Queries: ["logging"],
                        Snippets:
                        [
                            new(
                                SnippetId: "sn_a",
                                Heading: "Logging and OpenTelemetry",
                                Kind: "Section",
                                Language: null,
                                HitCount: 2,
                                Facets: ["Security"],
                                BestRank: 1,
                                BestScore: 0.0312,
                                IdentifierMatch: true,
                                Queries: ["otel"]
                            ),
                        ]
                    ),
                ],
                ToolUsage: [new(Tool: "search_sections", Calls: 2, Succeeded: 2, Failed: 0)],
                MissedQueries: []
            )
        );

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
              <review facet="Security" agent="Security Reviewer">
                <summary>Security summary</summary>
                <details>Security details</details>
                <findings>
                  <finding level="CRITICAL" summary="Critical issue" file="src/App.cs" line="42" side="RIGHT"><![CDATA[Critical body]]></finding>
                </findings>
              </review>
            </reviews>
            """;

    private sealed class Fixture
    {
        private Fixture() { }

        public TestCodeReviewRecordStore CodeReviews { get; } = new();

        public TestCodeReviewArtifactStore Artifacts { get; } = new();

        public GetCodeReviewFindingsHandler Handler { get; private set; } = null!;

        public static Fixture Create()
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
                            Role = "member",
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "usr_123",
                        },
                    ])
                );

            fixture.Handler = new(
                new CodeReviewAuthorization(memberships),
                fixture.CodeReviews,
                fixture.Artifacts,
                new CodeReviewXmlOutputValidator()
            );

            return fixture;
        }
    }

    private sealed class TestCodeReviewRecordStore : ICodeReviewRecordStore
    {
        public CodeReviewRecord Record { get; } =
            new()
            {
                Id = "cr_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                PullRequestRecordId = "pr_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "zeeq-ai/zeeq",
                PullRequestNumber = 8,
                Branch = "feature/findings-ui",
                Title = "Add findings UI",
                AuthorLogin = "octocat",
                Status = CodeReviewStatus.Completed,
                RequestOrigin = CodeReviewRequestOrigin.Manual,
                RemainingReviewBudget = 9,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

        public Task<CodeReviewRecord> AddAsync(
            CodeReviewRecord review,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Adding reviews is not used by these tests.");

        public Task<CodeReviewRecord> UpdateAsync(
            CodeReviewRecord review,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Updating reviews is not used by these tests.");

        public Task<CodeReviewRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Record.Id == id && Record.CreatedAtUtc == createdAtUtc
                    ? (CodeReviewRecord?)Record
                    : null
            );

        public Task<CodeReviewRecord?> FindNewestForPullRequestAsync(
            string organizationId,
            string pullRequestRecordId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Newest review lookup is not used by these tests.");

        public Task<CodeReviewStreamPage<CodeReviewRecord>> ListRecentAsync(
            CodeReviewStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Recent review listing is not used by these tests.");

        public Task<CodeReviewStreamPage<CodeReviewRecord>> ListForPullRequestAsync(
            PullRequestReviewStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("PR review listing is not used by these tests.");

        public Task<CodeReviewUpdateStreamPage> ListInboxUpdatesAsync(
            CodeReviewUpdateStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Inbox updates are not used by these tests.");

        public Task<IReadOnlyList<CodeReviewRecord>> ListForAgentAsync(
            string organizationId,
            string? agentSessionId,
            string? reviewGroupId,
            int maxRecords,
            CancellationToken cancellationToken
        ) => throw new NotImplementedException();
    }

    private sealed class TestCodeReviewArtifactStore : ICodeReviewArtifactStore
    {
        public string StorageUri { get; } =
            "postgres://code-review-findings/org_123/cr_123/202606250000000000000.xml";

        public int OpenCount { get; private set; }

        public string StoredXml { get; set; } = string.Empty;

        public Task<string> WriteFindingsAsync(
            CodeReviewRecord review,
            Stream findings,
            string contentType,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Writing findings is not used by these tests.");

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
        ) => throw new NotSupportedException("Copying findings is not used by these tests.");
    }
}
