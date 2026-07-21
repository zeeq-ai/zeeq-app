using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Verifies the pure GitHub comment DOM parser and renderer.
///
/// Run with:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubCommentDomRenderingTests/*"
/// </summary>
public sealed class GitHubCommentDomRenderingTests
{
    private static readonly GitHubCommentTargetSelector Target = new(
        OrganizationId: "org_123",
        RepositoryId: "repo_123",
        PullRequestNumber: 42,
        Kind: GitHubCommentTargetKind.PullRequestSummary,
        ScopeKey: "root"
    );

    [Test]
    public async Task Parser_WithEmptyBody_ReturnsEmptyDomForTarget()
    {
        // Guards first-write behavior: no existing GitHub comment still produces a target-aware DOM.
        var dom = GitHubCommentDomParser.Parse(Target, body: null);

        await Assert.That(dom.IsEmpty).IsTrue();
        await Assert.That(dom.RootMarker).IsEqualTo(GitHubCommentMarkers.PullRequestRoot);
        await Assert.That(dom.Sections).IsEmpty();
    }

    [Test]
    public async Task Parser_WithRankedComment_ParsesDirectChildSections()
    {
        // Guards the one-level DOM contract and rank capture from start markers.
        var body = """
            <!-- (000000):zeeq:pr-comment-root:start -->
            <!-- (100000):zeeq:pr-comment-header:start -->
            ## Zeeq Review
            <!-- zeeq:pr-comment-header:end -->

            <!-- (800000):zeeq:pr-findings:start -->
            Findings: 1 major.
            <!-- zeeq:pr-findings:end -->
            <!-- zeeq:pr-comment-root:end -->
            """;

        var dom = GitHubCommentDomParser.Parse(Target, body);

        await Assert.That(dom.IsEmpty).IsFalse();
        await Assert.That(dom.RootMarker).IsEqualTo(GitHubCommentMarkers.PullRequestRoot);
        await Assert.That(dom.Sections).Count().IsEqualTo(2);
        await Assert
            .That(dom.FindSection(GitHubCommentMarkers.PullRequestHeader)?.OrderKey)
            .IsEqualTo("100000");
        await Assert
            .That(dom.FindSection(GitHubCommentMarkers.PullRequestFindings)?.Content)
            .Contains("Findings: 1 major.");
    }

    [Test]
    public async Task Parser_WithNestedZeeqMarkerInsideSection_KeepsNestedMarkerAsRawMarkdown()
    {
        // Guards the explicit one-level-only rule. Nested marker text belongs to the parent section.
        var body = """
            <!-- (000000):zeeq:pr-comment-root:start -->
            <!-- (200000):zeeq:pr-comment-status:start -->
            Status contains literal marker text:
            <!-- (800000):zeeq:pr-findings:start -->
            Not a real child section here.
            <!-- zeeq:pr-findings:end -->
            <!-- zeeq:pr-comment-status:end -->
            <!-- zeeq:pr-comment-root:end -->
            """;

        var dom = GitHubCommentDomParser.Parse(Target, body);

        await Assert.That(dom.Sections).Count().IsEqualTo(1);
        await Assert
            .That(dom.FindSection(GitHubCommentMarkers.PullRequestStatus)?.Content)
            .Contains("Not a real child section here.");
        await Assert.That(dom.FindSection(GitHubCommentMarkers.PullRequestFindings)).IsNull();
    }

    [Test]
    public async Task Parser_WithSameMarkerNestedInsideSection_RejectsMalformedSection()
    {
        // Guards that same-marker nesting is not accepted as raw Markdown or guessed into a section.
        var body = """
            <!-- (000000):zeeq:pr-comment-root:start -->
            <!-- (200000):zeeq:pr-comment-status:start -->
            Status contains malformed same-marker text:
            <!-- (200000):zeeq:pr-comment-status:start -->
            Inner text is not a supported nested section.
            <!-- zeeq:pr-comment-status:end -->
            Tail text should not be guessed into a section.
            <!-- zeeq:pr-comment-status:end -->
            <!-- (800000):zeeq:pr-findings:start -->
            Findings remain parseable.
            <!-- zeeq:pr-findings:end -->
            <!-- zeeq:pr-comment-root:end -->
            """;

        var dom = GitHubCommentDomParser.Parse(Target, body);

        await Assert.That(dom.FindSection(GitHubCommentMarkers.PullRequestStatus)).IsNull();
        await Assert
            .That(dom.FindSection(GitHubCommentMarkers.PullRequestFindings)?.Content)
            .Contains("Findings remain parseable.");
    }

    [Test]
    public async Task Renderer_WithPatch_PreservesUnpatchedExistingSections()
    {
        // Guards the main DOM guarantee: rendering one section must not delete unrelated sections.
        var dom = ExistingDom(
            Section(GitHubCommentMarkers.PullRequestHeader, "100000", "Existing header"),
            Section(GitHubCommentMarkers.PullRequestFindings, "800000", "Existing findings")
        );
        var renderer = new GitHubCommentDomRenderer([
            StaticRenderer.Replace(
                GitHubCommentMarkers.PullRequestStatus,
                "200000",
                "Review queued."
            ),
        ]);

        var body = renderer.Render(
            kind: "queued",
            clear: [],
            context: EmptyContext(),
            currentDom: dom
        );

        await Assert.That(body).Contains("Existing header");
        await Assert.That(body).Contains("Existing findings");
        await Assert.That(body).Contains("Review queued.");
    }

    [Test]
    public async Task Renderer_WithClosedKind_DoesNotCreateClosedStatusOrRemoveExistingFindings()
    {
        var dom = ExistingDom(
            Section(GitHubCommentMarkers.PullRequestStatus, "200000", "Existing status"),
            Section(GitHubCommentMarkers.PullRequestFindings, "300000", "Existing findings")
        );
        var renderer = new GitHubCommentDomRenderer([new PullRequestStatusSectionRenderer()]);

        var body = renderer.Render(
            kind: "closed",
            clear: [],
            context: EmptyContext(),
            currentDom: dom
        );

        await Assert.That(body).Contains("Existing status");
        await Assert.That(body).Contains("Existing findings");
        await Assert.That(body).DoesNotContain("PR is closed or merged");
    }

    [Test]
    public async Task Renderer_WithClear_RemovesSectionBeforePatches()
    {
        // Guards message-level Clear semantics for stale sections from older review attempts.
        var dom = ExistingDom(
            Section(GitHubCommentMarkers.PullRequestHeader, "100000", "Existing header"),
            Section(GitHubCommentMarkers.PullRequestFindings, "800000", "Stale findings")
        );
        var renderer = new GitHubCommentDomRenderer([]);

        var body = renderer.Render(
            kind: "queued",
            clear: [GitHubCommentMarkers.PullRequestFindings],
            context: EmptyContext(),
            currentDom: dom
        );

        await Assert.That(body).Contains("Existing header");
        await Assert.That(body).DoesNotContain("Stale findings");
        await Assert.That(body).DoesNotContain(GitHubCommentMarkers.PullRequestFindings);
    }

    [Test]
    public async Task Renderer_WithClearAndPatchForSameSection_ReaddsSection()
    {
        // Guards operation order: Clear runs first, then explicit patches can recreate the section.
        var dom = ExistingDom(
            Section(GitHubCommentMarkers.PullRequestFindings, "800000", "Stale findings")
        );
        var renderer = new GitHubCommentDomRenderer([
            StaticRenderer.Replace(
                GitHubCommentMarkers.PullRequestFindings,
                orderKey: null,
                markdown: "Fresh findings"
            ),
        ]);

        var body = renderer.Render(
            kind: "review_completed",
            clear: [GitHubCommentMarkers.PullRequestFindings],
            context: EmptyContext(),
            currentDom: dom
        );

        await Assert.That(body).Contains("Fresh findings");
        await Assert.That(body).DoesNotContain("Stale findings");
        await Assert
            .That(body)
            .Contains($"<!-- (300000):{GitHubCommentMarkers.PullRequestFindings}:start -->");
    }

    [Test]
    public async Task Renderer_WithOutOfOrderRendererRegistration_RendersByOrderKey()
    {
        // Guards that document order is data-driven by rank, not DI registration order.
        var dom = GitHubCommentDom.Empty(Target);
        var renderer = new GitHubCommentDomRenderer([
            StaticRenderer.Replace(GitHubCommentMarkers.PullRequestFooter, "zzzzzz", "Footer"),
            StaticRenderer.Replace(GitHubCommentMarkers.PullRequestHeader, "100000", "Header"),
            StaticRenderer.Replace(GitHubCommentMarkers.PullRequestStatus, "200000", "Status"),
        ]);

        var body = renderer.Render(
            kind: "queued",
            clear: [],
            context: EmptyContext(),
            currentDom: dom
        );

        await Assert
            .That(body.IndexOf("Header", StringComparison.Ordinal))
            .IsLessThan(body.IndexOf("Status", StringComparison.Ordinal));
        await Assert
            .That(body.IndexOf("Status", StringComparison.Ordinal))
            .IsLessThan(body.IndexOf("Footer", StringComparison.Ordinal));
    }

    [Test]
    public async Task Renderer_WithCompletedReview_RendersV1StyleGitHubCommentSections()
    {
        var xml = SampleFindingsXml();
        var validation = new CodeReviewXmlOutputValidator().Validate(xml);
        var context = new CodeReviewCommentRenderContext(
            Review: ReviewRecord(),
            FindingsXml: xml,
            Findings: validation.Output,
            FindingsLoadError: null,
            ActionLinks: new("https://zeeq.test/review?kind=rerequest"),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z")
        );
        var renderer = new GitHubCommentDomRenderer([
            new PullRequestHeaderSectionRenderer(),
            new PullRequestStatusSectionRenderer(),
            new PullRequestFindingsSectionRenderer(),
            new PullRequestEvidenceSectionRenderer(),
            new PullRequestActionsSectionRenderer(),
            new PullRequestFooterSectionRenderer(
                new StaticCodeReviewRuntimeStatistics(
                    new(
                        SampleCount: 2,
                        Percentile50: TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(2),
                        Percentile95: TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(4)
                    )
                )
            ),
        ]);

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: context,
            currentDom: GitHubCommentDom.Empty(Target)
        );
        var renderedDom = GitHubCommentDomParser.Parse(Target, body);
        var findingsContent = renderedDom
            .FindSection(GitHubCommentMarkers.PullRequestFindings)
            ?.Content;

        await Assert.That(body).Contains("👋 Hi there");
        await Assert.That(body).Contains("## STRUCTURAL REVIEW (1 finding)");
        await Assert
            .That(body)
            .Contains("<summary>❮EXPAND❯ LGTM overall; one structural concern.</summary>");
        await Assert.That(body).Contains("> The structure is mostly sound.");
        await Assert
            .That(body)
            .Contains("### 👉 «COMMENT» Server-owned selected rollup id is mirrored");
        await Assert
            .That(body)
            .Contains("(File: `web/apps/wonderly-app/src/signals.ts`, Line: 206, Side: RIGHT)");
        await Assert.That(body).Contains("Finding body before");
        var structuralSummaryIndex = body.IndexOf(
            "<summary>❮EXPAND❯ LGTM overall; one structural concern.</summary>",
            StringComparison.Ordinal
        );
        var structuralFindingIndex = body.IndexOf(
            "### 👉 «COMMENT» Server-owned selected rollup id is mirrored",
            StringComparison.Ordinal
        );
        var structuralFindingBodyIndex = body.IndexOf(
            "Finding body before",
            StringComparison.Ordinal
        );
        var structuralDetailsEndIndex = body.IndexOf(
            "</details>",
            structuralSummaryIndex,
            StringComparison.Ordinal
        );
        await Assert.That(structuralSummaryIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(structuralFindingIndex).IsGreaterThan(structuralSummaryIndex);
        await Assert.That(structuralFindingBodyIndex).IsGreaterThan(structuralFindingIndex);
        await Assert.That(structuralDetailsEndIndex).IsGreaterThan(structuralFindingBodyIndex);
        await Assert.That(findingsContent).DoesNotContain("zeeq:pr-findings:start");
        await Assert.That(body).Contains("|***TOTAL***|***0***|***0***|***1***|***1***|***1***|");
        await Assert
            .That(body)
            .Contains("<summary>❮EXPAND❯ Raw review findings XML for agents</summary>");
        await Assert.That(body).Contains("````xml");
        await Assert.That(body).Contains("<reviews noAgentsActivated=\"false\"");
        await Assert
            .That(body)
            .Contains("[Request another Zeeq review](https://zeeq.test/review?kind=rerequest)");
        await Assert.That(body).Contains("*(Updated at 2026-06-25 17:19:43 UTC)*");
        await Assert.That(body).Contains("50th percentile runtime: 01:02; 95th: 03:04");
    }

    [Test]
    public async Task Renderer_WithFindingsLoadError_RendersControlledStatusWithoutFindings()
    {
        var context = new CodeReviewCommentRenderContext(
            Review: ReviewRecord(),
            FindingsXml: null,
            Findings: null,
            FindingsLoadError: "Findings XML could not be validated.",
            ActionLinks: new(),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z")
        );
        var renderer = new GitHubCommentDomRenderer([
            new PullRequestStatusSectionRenderer(),
            new PullRequestFindingsSectionRenderer(),
        ]);

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: context,
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert.That(body).Contains("Review completed, but Zeeq could not load");
        await Assert.That(body).Contains("Findings XML could not be validated.");
        await Assert.That(body).DoesNotContain("## STRUCTURAL REVIEW");
    }

    [Test]
    public async Task Renderer_WithNoFindings_RendersCleanPrTipNoiceAndNoRawXml()
    {
        var xml = SampleNoFindingsXml();
        var validation = new CodeReviewXmlOutputValidator().Validate(xml);
        var context = new CodeReviewCommentRenderContext(
            Review: ReviewRecord(),
            FindingsXml: xml,
            Findings: validation.Output,
            FindingsLoadError: null,
            ActionLinks: new(NoiceImageUrl: "https://zeeq.test/noice.webp"),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z"),
            ShowNoice: true
        );
        var renderer = new GitHubCommentDomRenderer([
            new PullRequestStatusSectionRenderer(),
            new PullRequestFindingsSectionRenderer(),
            new PullRequestEvidenceSectionRenderer(),
        ]);

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: context,
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert.That(body).Contains("> [!TIP]");
        await Assert.That(body).Contains("Zero findings from the 2 reviewers participating");
        await Assert.That(body).Contains("<summary>❮EXPAND❯ Noice!</summary>");
        await Assert
            .That(body)
            .Contains("<p align=\"center\"><img src=\"https://zeeq.test/noice.webp\" /></p>");
        await Assert.That(body).DoesNotContain("Raw review findings XML");
        await Assert.That(body).DoesNotContain("## STRUCTURAL REVIEW");
    }

    [Test]
    public async Task Renderer_WithNoAgentsActivated_RendersInfoAdmonitionAndNoRawXml()
    {
        var xml = SampleNoAgentsFindingsXml();
        var validation = new CodeReviewXmlOutputValidator().Validate(xml);
        var context = new CodeReviewCommentRenderContext(
            Review: ReviewRecord(),
            FindingsXml: xml,
            Findings: validation.Output,
            FindingsLoadError: null,
            ActionLinks: new(),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z")
        );
        var renderer = new GitHubCommentDomRenderer([
            new PullRequestStatusSectionRenderer(),
            new PullRequestFindingsSectionRenderer(),
            new PullRequestEvidenceSectionRenderer(),
        ]);

        var body = renderer.Render(
            kind: "no_agents_activated",
            clear: [],
            context: context,
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert.That(body).Contains("> [!NOTE]");
        await Assert.That(body).Contains("No configured code review agents activated");
        await Assert.That(body).DoesNotContain("Raw review findings XML");
        await Assert.That(body).DoesNotContain("## NO REVIEWER AGENTS ACTIVATED");
    }

    [Test]
    public async Task FooterRenderer_WithNoRuntimeData_RendersNoDataFallback()
    {
        var renderer = new GitHubCommentDomRenderer([
            new PullRequestFooterSectionRenderer(
                new StaticCodeReviewRuntimeStatistics(CodeReviewRuntimePercentilesSnapshot.NoData)
            ),
        ]);

        var body = renderer.Render(
            kind: "queued",
            clear: [],
            context: EmptyContext(),
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert.That(body).Contains("50th percentile runtime: (no data); 95th: (no data)");
    }

    [Test]
    public async Task HeaderRenderer_WithViewReviewUrl_RendersManageLink()
    {
        var renderer = new GitHubCommentDomRenderer([new PullRequestHeaderSectionRenderer()]);
        var context = new CodeReviewCommentRenderContext(
            Review: null,
            FindingsXml: null,
            Findings: null,
            FindingsLoadError: null,
            ActionLinks: new(ViewReviewUrl: "https://app.zeeq.ai/code-reviews/reviews/cr_abc"),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z")
        );

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: context,
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert
            .That(body)
            .Contains("[Manage this PR in Zeeq](https://app.zeeq.ai/code-reviews/reviews/cr_abc)");
    }

    [Test]
    public async Task HeaderRenderer_WithoutViewReviewUrl_OmitsManageLink()
    {
        var renderer = new GitHubCommentDomRenderer([new PullRequestHeaderSectionRenderer()]);

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: EmptyContext(),
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert.That(body).DoesNotContain("Manage this PR in Zeeq");
    }

    [Test]
    public async Task FooterRenderer_WithViewReviewUrl_RendersLink()
    {
        var renderer = new GitHubCommentDomRenderer([
            new PullRequestFooterSectionRenderer(
                new StaticCodeReviewRuntimeStatistics(CodeReviewRuntimePercentilesSnapshot.NoData)
            ),
        ]);
        var context = new CodeReviewCommentRenderContext(
            Review: null,
            FindingsXml: null,
            Findings: null,
            FindingsLoadError: null,
            ActionLinks: new(ViewReviewUrl: "https://app.zeeq.ai/code-reviews/reviews/cr_abc"),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z")
        );

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: context,
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert
            .That(body)
            .Contains(
                "[View this review in Zeeq](https://app.zeeq.ai/code-reviews/reviews/cr_abc)"
            );
    }

    [Test]
    public async Task FooterRenderer_WithoutViewReviewUrl_OmitsLink()
    {
        var renderer = new GitHubCommentDomRenderer([
            new PullRequestFooterSectionRenderer(
                new StaticCodeReviewRuntimeStatistics(CodeReviewRuntimePercentilesSnapshot.NoData)
            ),
        ]);

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: EmptyContext(),
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert.That(body).DoesNotContain("View this review in Zeeq");
    }

    [Test]
    public async Task Renderer_WithCriticalAndMajorFindings_RendersSeverityAdmonitions()
    {
        var xml = SampleCriticalAndMajorFindingsXml();
        var validation = new CodeReviewXmlOutputValidator().Validate(xml);
        var context = new CodeReviewCommentRenderContext(
            Review: ReviewRecord(),
            FindingsXml: xml,
            Findings: validation.Output,
            FindingsLoadError: null,
            ActionLinks: new(),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z")
        );
        var renderer = new GitHubCommentDomRenderer([new PullRequestFindingsSectionRenderer()]);

        var body = renderer.Render(
            kind: "review_completed",
            clear: [],
            context: context,
            currentDom: GitHubCommentDom.Empty(Target)
        );

        await Assert.That(body).Contains("> [!CAUTION]");
        await Assert.That(body).Contains("Encountered 1 critical finding");
        await Assert.That(body).Contains("> [!WARNING]");
        await Assert.That(body).Contains("Encountered 1 major finding");
    }

    private static GitHubCommentDom ExistingDom(params GitHubCommentDomSection[] sections) =>
        new(Target, GitHubCommentMarkers.PullRequestRoot, sections);

    private static GitHubCommentDomSection Section(
        string marker,
        string orderKey,
        string content
    ) =>
        new()
        {
            Marker = marker,
            OrderKey = orderKey,
            Content = content,
        };

    private static CodeReviewCommentRenderContext EmptyContext() =>
        new(
            Review: null,
            FindingsXml: null,
            Findings: null,
            FindingsLoadError: null,
            ActionLinks: new(),
            RenderedAtUtc: DateTimeOffset.Parse("2026-06-25T17:19:43Z")
        );

    private sealed class StaticCodeReviewRuntimeStatistics(
        CodeReviewRuntimePercentilesSnapshot snapshot
    ) : ICodeReviewRuntimeStatistics
    {
        public ValueTask RecordAsync(TimeSpan runtime, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public CodeReviewRuntimePercentilesSnapshot GetSnapshot() => snapshot;
    }

    private static CodeReviewRecord ReviewRecord() =>
        new()
        {
            Id = "cr_123",
            OrganizationId = "org_123",
            TeamId = "team_123",
            PullRequestRecordId = "pr_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            PullRequestNumber = 42,
            Branch = "feature/comments",
            Title = "Render comments",
            AuthorLogin = "octocat",
            Status = CodeReviewStatus.Completed,
            RequestOrigin = CodeReviewRequestOrigin.RepositoryWebhook,
            RemainingReviewBudget = 1,
            CreatedAtUtc = DateTimeOffset.Parse("2026-06-25T17:00:00Z"),
            UpdatedAtUtc = DateTimeOffset.Parse("2026-06-25T17:00:00Z"),
        };

    private static string SampleFindingsXml() =>
        CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument
            {
                Reviews =
                [
                    new()
                    {
                        Facet = "Structural",
                        Agent = "Structural Reviewer",
                        Summary = "LGTM overall; one structural concern.",
                        Details = "The structure is mostly sound.",
                        Findings =
                        [
                            new()
                            {
                                Level = CodeReviewFindingLevel.Comment,
                                File = "web/apps/wonderly-app/src/signals.ts",
                                Line = 206,
                                Side = "RIGHT",
                                Summary = "Server-owned selected rollup id is mirrored",
                                Details =
                                    "Finding body before <!-- (300000):zeeq:pr-findings:start --> hidden <!-- zeeq:pr-findings:end --> after.",
                            },
                        ],
                    },
                    new()
                    {
                        Facet = "Test",
                        Agent = "Test Reviewer",
                        Summary = "Mostly good coverage.",
                        Details = "A small test concern.",
                        Findings =
                        [
                            new()
                            {
                                Level = CodeReviewFindingLevel.Minor,
                                File = "tests/file.cs",
                                Line = 77,
                                Side = "RIGHT",
                                Summary = "Avoid brittle assertion",
                                Details = "Split the assertion.",
                            },
                        ],
                    },
                    new()
                    {
                        Facet = "Performance",
                        Agent = "Performance Reviewer",
                        Summary = "One small optimization note.",
                        Details = "A small DOM payload concern.",
                        Findings =
                        [
                            new()
                            {
                                Level = CodeReviewFindingLevel.Suggestion,
                                File = "src/component.astro",
                                Line = 22,
                                Side = "RIGHT",
                                Summary = "Avoid extra active bindings",
                                Details = "Branch by active mode.",
                            },
                        ],
                    },
                ],
            }
        );

    private static string SampleNoFindingsXml() =>
        CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument
            {
                Reviews =
                [
                    new()
                    {
                        Facet = "Structural",
                        Agent = "Structural Reviewer",
                        Summary = "Looks clean.",
                        Details = "No concerns found.",
                        Findings = [],
                    },
                    new()
                    {
                        Facet = "Performance",
                        Agent = "Performance Reviewer",
                        Summary = "Looks clean.",
                        Details = "No concerns found.",
                        Findings = [],
                    },
                ],
            }
        );

    private static string SampleNoAgentsFindingsXml() =>
        CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument { NoAgentsActivated = true }
        );

    private static string SampleCriticalAndMajorFindingsXml() =>
        CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument
            {
                Reviews =
                [
                    new()
                    {
                        Facet = "Structural",
                        Agent = "Structural Reviewer",
                        Summary = "Blocking concerns.",
                        Details = "A critical and major issue were found.",
                        Findings =
                        [
                            new()
                            {
                                Level = CodeReviewFindingLevel.Critical,
                                File = "src/service.cs",
                                Line = 10,
                                Side = "RIGHT",
                                Summary = "Critical data loss",
                                Details = "This can lose data.",
                            },
                            new()
                            {
                                Level = CodeReviewFindingLevel.Major,
                                File = "src/service.cs",
                                Line = 20,
                                Side = "RIGHT",
                                Summary = "Major correctness issue",
                                Details = "This can return the wrong result.",
                            },
                        ],
                    },
                ],
            }
        );

    private sealed class StaticRenderer(GitHubCommentDomPatch patch) : IGitHubCommentSectionRenderer
    {
        public string SectionKind => patch.SectionKind;

        public GitHubCommentDomPatch? Render(
            string kind,
            CodeReviewCommentRenderContext context,
            GitHubCommentDom currentDom
        ) => patch;

        public static StaticRenderer Replace(
            string sectionKind,
            string? orderKey,
            string markdown
        ) =>
            new(
                new GitHubCommentDomPatch(
                    SectionKind: sectionKind,
                    OrderKey: orderKey,
                    Mode: GitHubCommentPatchMode.ReplaceSection,
                    Markdown: markdown
                )
            );
    }
}
