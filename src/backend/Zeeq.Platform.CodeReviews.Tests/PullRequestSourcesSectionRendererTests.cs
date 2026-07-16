using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests the PR comment "documents and sources consulted" section renderer: it renders a collapsed
/// details block for completed review kinds regardless of finding count, orders documents as the
/// snapshot provides them, and removes the section when there is no telemetry.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/PullRequestSourcesSectionRendererTests/*"
/// </summary>
public sealed class PullRequestSourcesSectionRendererTests
{
    private static readonly GitHubCommentTargetSelector Target = new(
        OrganizationId: "org_123",
        RepositoryId: "repo_123",
        PullRequestNumber: 42,
        Kind: GitHubCommentTargetKind.PullRequestSummary,
        ScopeKey: "root"
    );

    private static readonly PullRequestSourcesSectionRenderer Renderer = new();

    [Test]
    public async Task Render_WithTelemetry_ProducesCollapsedDetailsWithDocumentAndToolTables()
    {
        var patch = Renderer.Render("review_completed", Context(PopulatedTelemetry()), Dom());

        await Assert.That(patch).IsNotNull();
        await Assert.That(patch!.SectionKind).IsEqualTo(GitHubCommentMarkers.PullRequestSources);
        await Assert.That(patch.Mode).IsEqualTo(GitHubCommentPatchMode.ReplaceSection);

        var markdown = patch.Markdown!;
        await Assert.That(markdown).StartsWith("<details>");
        await Assert.That(markdown).Contains("Documents consulted");
        await Assert.That(markdown).Contains("zeeq://backend/dotnet-csharp-best-practices.md");
        await Assert.That(markdown).Contains("Tool usage");
        // Underscores in the tool name are backslash-escaped for Markdown text safety.
        await Assert.That(markdown).Contains("search\\_sections");
        // Snippet bullet with kind descriptor rendered as a superscript.
        await Assert.That(markdown).Contains("Logging and OpenTelemetry (OTEL) Tracing");
        await Assert.That(markdown).Contains("<sup>Section</sup>");
        await Assert.That(markdown).Contains("<sup>CodeSample csharp</sup>");
        // The read column uses a marker for both states so neither reads as missing data.
        await Assert.That(markdown).Contains("| 5 | ✓ |");
        await Assert.That(markdown).Contains("| 2 | — |");
        await Assert.That(markdown).EndsWith("</details>");
    }

    [Test]
    public async Task Render_OrdersDocumentsAsSnapshotProvides()
    {
        var markdown = Renderer
            .Render("review_completed", Context(PopulatedTelemetry()), Dom())!
            .Markdown!;

        // The snapshot lists the higher-hit document first; the renderer preserves that order.
        var firstIndex = markdown.IndexOf(
            "dotnet-csharp-best-practices.md",
            StringComparison.Ordinal
        );
        var secondIndex = markdown.IndexOf(
            "web-api-endpoints-openapi.md",
            StringComparison.Ordinal
        );

        await Assert.That(firstIndex).IsGreaterThan(0);
        await Assert.That(secondIndex).IsGreaterThan(firstIndex);
    }

    [Test]
    public async Task Render_WithMissedQueries_IncludesContentGaps()
    {
        var markdown = Renderer
            .Render("review_completed", Context(PopulatedTelemetry()), Dom())!
            .Markdown!;

        await Assert.That(markdown).Contains("Content gaps");
        await Assert.That(markdown).Contains("aspire distributed lock pattern");
    }

    [Test]
    public async Task Render_AtZeroFindings_StillRenders()
    {
        // The context carries no findings at all, but telemetry is present — the section must still
        // render so a clean-PR conclusion shows how it was reached.
        var context = Context(PopulatedTelemetry());

        var patch = Renderer.Render("review_completed", context, Dom());

        await Assert.That(patch).IsNotNull();
        await Assert.That(patch!.Mode).IsEqualTo(GitHubCommentPatchMode.ReplaceSection);
        await Assert.That(context.Findings).IsNull();
    }

    [Test]
    [Arguments("no_agents_activated")]
    [Arguments("stub_review_completed")]
    public async Task Render_ForOtherCompletedKinds_Renders(string kind)
    {
        var patch = Renderer.Render(kind, Context(PopulatedTelemetry()), Dom());

        await Assert.That(patch).IsNotNull();
        await Assert.That(patch!.Mode).IsEqualTo(GitHubCommentPatchMode.ReplaceSection);
    }

    [Test]
    public async Task Render_WhenTelemetryNull_RemovesSection()
    {
        var patch = Renderer.Render("review_completed", Context(null), Dom());

        await Assert.That(patch).IsNotNull();
        await Assert.That(patch!.Mode).IsEqualTo(GitHubCommentPatchMode.RemoveSection);
        await Assert.That(patch.Markdown).IsNull();
    }

    [Test]
    public async Task Render_WhenTelemetryEmpty_RemovesSection()
    {
        var patch = Renderer.Render(
            "review_completed",
            Context(CodeReviewSourceTelemetry.Empty),
            Dom()
        );

        await Assert.That(patch).IsNotNull();
        await Assert.That(patch!.Mode).IsEqualTo(GitHubCommentPatchMode.RemoveSection);
    }

    [Test]
    [Arguments("queued")]
    [Arguments("running")]
    [Arguments("review_failed")]
    public async Task Render_WhenNotCompletedKind_ReturnsNull(string kind)
    {
        var patch = Renderer.Render(kind, Context(PopulatedTelemetry()), Dom());

        await Assert.That(patch).IsNull();
    }

    [Test]
    public async Task Render_EscapesMarkdownSpecialsInText_AndNeutralizesBackticksInCodeSpans()
    {
        var telemetry = new CodeReviewSourceTelemetry(
            SchemaVersion: CodeReviewSourceTelemetry.CurrentSchemaVersion,
            Summary: new(1, 1, 2, 1, 1),
            Documents:
            [
                new(
                    DocumentId: "doc_x",
                    Library: "zeeq-app",
                    Path: "/backend/dotnet-csharp-best-practices.md",
                    Title: "Functional Programming, `Action`, `Func<T>`",
                    HitCount: 2,
                    Usages: ["Searched"],
                    ReadAfterSearch: false,
                    Facets: ["Security"],
                    BestRank: 1,
                    BestScore: 0.01,
                    Queries: [],
                    Snippets:
                    [
                        new(
                            SnippetId: "sn_x",
                            Heading: "Heading with `code` and _under_score",
                            Kind: "Section",
                            Language: null,
                            HitCount: 1,
                            Facets: ["Security"],
                            BestRank: 1,
                            BestScore: 0.01,
                            IdentifierMatch: false,
                            Queries: []
                        ),
                    ]
                ),
            ],
            ToolUsage: [new(Tool: "search_sections", Calls: 1, Succeeded: 1, Failed: 0)],
            MissedQueries:
            [
                new(Query: "weird `backtick` query", Tool: "search_sections", Facets: ["Security"]),
            ]
        );

        var markdown = Renderer.Render("review_completed", Context(telemetry), Dom())!.Markdown!;

        // Inline-significant characters in text positions are backslash-escaped verbatim.
        await Assert
            .That(markdown)
            .Contains("Functional Programming, \\`Action\\`, \\`Func\\<T\\>\\`");
        await Assert.That(markdown).Contains("Heading with \\`code\\` and \\_under\\_score");
        // A backtick inside an agent query cannot break the inline code span.
        await Assert.That(markdown).Contains("`weird 'backtick' query`");
        await Assert.That(markdown).DoesNotContain("weird `backtick` query");
    }

    [Test]
    public async Task Render_DropsLeadingHeadingSegmentFromSnippetBullets()
    {
        // The leading heading segment repeats the bold document label above the bullets, so the
        // renderer drops it and keeps the more specific tail (deeper separators are preserved).
        var telemetry = new CodeReviewSourceTelemetry(
            SchemaVersion: CodeReviewSourceTelemetry.CurrentSchemaVersion,
            Summary: new(1, 2, 2, 1, 0),
            Documents:
            [
                new(
                    DocumentId: "doc_h",
                    Library: "zeeq-app",
                    Path: "/devtools/csharprepl-runtime-live-debugging.md",
                    Title: "CSharpRepl Runtime Debugging",
                    HitCount: 2,
                    Usages: ["Searched"],
                    ReadAfterSearch: false,
                    Facets: ["Logical"],
                    BestRank: 1,
                    BestScore: 0.02,
                    Queries: [],
                    Snippets:
                    [
                        new(
                            SnippetId: "sn_leaf",
                            Heading: "CSharpRepl Runtime Debugging > Resolve and Call Services",
                            Kind: "CodeSample",
                            Language: "sh",
                            HitCount: 1,
                            Facets: ["Logical"],
                            BestRank: 1,
                            BestScore: 0.02,
                            IdentifierMatch: false,
                            Queries: []
                        ),
                        new(
                            SnippetId: "sn_deep",
                            Heading: "CSharpRepl Runtime Debugging > Debugging Recipes > Reusable probe scripts",
                            Kind: "CodeSample",
                            Language: "csharp",
                            HitCount: 1,
                            Facets: ["Logical"],
                            BestRank: 2,
                            BestScore: 0.01,
                            IdentifierMatch: false,
                            Queries: []
                        ),
                    ]
                ),
            ],
            ToolUsage: [new(Tool: "search_sections", Calls: 1, Succeeded: 1, Failed: 0)],
            MissedQueries: []
        );

        var markdown = Renderer.Render("review_completed", Context(telemetry), Dom())!.Markdown!;

        // Bullets show the tail after the first segment (">" is backslash-escaped in text).
        await Assert
            .That(markdown)
            .Contains("- Resolve and Call Services <sup>CodeSample sh</sup>");
        await Assert
            .That(markdown)
            .Contains(
                "- Debugging Recipes \\> Reusable probe scripts <sup>CodeSample csharp</sup>"
            );
        // The leading segment survives only as the bold document label, never on a bullet line.
        await Assert.That(markdown).DoesNotContain("- CSharpRepl Runtime Debugging");
    }

    private static GitHubCommentDom Dom() => GitHubCommentDom.Empty(Target);

    private static CodeReviewCommentRenderContext Context(CodeReviewSourceTelemetry? telemetry) =>
        new(
            Review: null,
            FindingsXml: null,
            Findings: null,
            FindingsLoadError: null,
            ActionLinks: new(),
            RenderedAtUtc: DateTimeOffset.UnixEpoch,
            SourceTelemetry: telemetry
        );

    private static CodeReviewSourceTelemetry PopulatedTelemetry() =>
        new(
            SchemaVersion: CodeReviewSourceTelemetry.CurrentSchemaVersion,
            Summary: new(
                DocumentCount: 2,
                SnippetCount: 2,
                SourceHitCount: 7,
                ToolCallCount: 4,
                MissedQueryCount: 1
            ),
            Documents:
            [
                new(
                    DocumentId: "doc_01H",
                    Library: "zeeq-app",
                    Path: "/backend/dotnet-csharp-best-practices.md",
                    Title: "C# 14, .NET 10, EF General Guidelines",
                    HitCount: 5,
                    Usages: ["Read", "Searched"],
                    ReadAfterSearch: true,
                    Facets: ["Performance", "Security"],
                    BestRank: 1,
                    BestScore: 0.0312,
                    Queries: ["structured logging"],
                    Snippets:
                    [
                        new(
                            SnippetId: "sn_01H",
                            Heading: "Logging and OpenTelemetry (OTEL) Tracing",
                            Kind: "Section",
                            Language: null,
                            HitCount: 3,
                            Facets: ["Security"],
                            BestRank: 1,
                            BestScore: 0.0312,
                            IdentifierMatch: true,
                            Queries: ["otel tracing"]
                        ),
                        new(
                            SnippetId: "sn_02J",
                            Heading: "Database Storage with Postgres and Npgsql",
                            Kind: "CodeSample",
                            Language: "csharp",
                            HitCount: 1,
                            Facets: ["Performance"],
                            BestRank: 4,
                            BestScore: 0.0121,
                            IdentifierMatch: false,
                            Queries: ["npgsql batching"]
                        ),
                    ]
                ),
                new(
                    DocumentId: "doc_01J",
                    Library: "zeeq-app",
                    Path: "/backend/web-api-endpoints-openapi.md",
                    Title: "Web API Endpoints and OpenAPI",
                    HitCount: 2,
                    Usages: ["Read"],
                    ReadAfterSearch: false,
                    Facets: ["Structural"],
                    BestRank: 0,
                    BestScore: 0,
                    Queries: [],
                    Snippets: []
                ),
            ],
            ToolUsage:
            [
                new(Tool: "search_sections", Calls: 2, Succeeded: 2, Failed: 0),
                new(Tool: "read_document_by_path", Calls: 2, Succeeded: 2, Failed: 0),
            ],
            MissedQueries:
            [
                new(
                    Query: "aspire distributed lock pattern",
                    Tool: "search_sections",
                    Facets: ["Structural"]
                ),
            ]
        );
}
