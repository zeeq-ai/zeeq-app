using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests for CodeReviewAgentExecutor prompt-building helpers:
/// BuildPreviousReviewsSection, IsSameFacet, and NormalizeFacet.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewPreviousReviewPromptTests/*"
/// </summary>
public sealed class CodeReviewPreviousReviewPromptTests
{
    private static CodeReviewerRuntimeAgent Reviewer(
        string displayName = "Security Reviewer",
        string facet = "Security"
    ) =>
        new(
            Id: "agent_1",
            DisplayName: displayName,
            ReviewFacet: facet,
            ModelTier: CodeReviewModelTier.High,
            Prompt: "Review for security issues.",
            ActivationConfiguration: CodeReviewerActivationConfiguration.Empty
        );

    private static CodeReviewPreviousReview PreviousReview(
        string facet = "Security",
        string summary = "Previous security review",
        params CodeReviewPreviousFinding[] findings
    ) => new(Facet: facet, Summary: summary, Findings: findings);

    private static CodeReviewPreviousFinding Finding(
        CodeReviewFindingLevel level,
        string summary = "A finding",
        string body = "Finding body",
        string file = "src/test.cs"
    ) => new(Summary: summary, Details: body, File: file, Level: level);

    // BuildPreviousReviewsSection tests

    [Test]
    public async Task BuildPreviousReviewsSection_EmptyList_ReturnsEmpty()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(Reviewer(), []);

        await Assert.That(section).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_NoFacetMatch_ReturnsEmpty()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(facet: "Security"),
            [PreviousReview(facet: "Performance", findings: Finding(CodeReviewFindingLevel.Major))]
        );

        await Assert.That(section).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_MatchButZeroFindings_ReturnsEmpty()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(facet: "Security"),
            [PreviousReview(facet: "Security", findings: [])]
        );

        await Assert.That(section).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_MultipleMatchingReviews_AggregatesXml()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(facet: "Security"),
            [
                PreviousReview(
                    facet: "Security",
                    summary: "Review 1",
                    findings: [Finding(CodeReviewFindingLevel.Critical, "Issue 1")]
                ),
                PreviousReview(
                    facet: "Security",
                    summary: "Review 2",
                    findings: [Finding(CodeReviewFindingLevel.Minor, "Issue 2")]
                ),
            ]
        );

        await Assert.That(section).Contains("Review 1");
        await Assert.That(section).Contains("Review 2");
    }

    [Test]
    public async Task BuildPreviousReviewsSection_OnlyMatchingFacetIncluded()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(facet: "Security"),
            [
                PreviousReview(
                    facet: "Performance",
                    summary: "Perf review",
                    findings: [Finding(CodeReviewFindingLevel.Major)]
                ),
                PreviousReview(
                    facet: "Security",
                    summary: "Security review",
                    findings: [Finding(CodeReviewFindingLevel.Critical)]
                ),
            ]
        );

        await Assert.That(section).Contains("Security review");
        await Assert.That(section).DoesNotContain("Perf review");
    }

    // IsSameFacet tests

    [Test]
    public async Task BuildPreviousReviewsSection_FacetCasingDifferent_Matches()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(facet: "security"),
            [PreviousReview(facet: "Security", findings: [Finding(CodeReviewFindingLevel.Major)])]
        );

        await Assert.That(section).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_FacetWithSpaces_Matches()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(facet: "CodeQuality"),
            [
                PreviousReview(
                    facet: "Code Quality",
                    findings: [Finding(CodeReviewFindingLevel.Major)]
                ),
            ]
        );

        await Assert.That(section).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_ReviewerNameMatchesFacet_Matches()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(displayName: "General Reviewer", facet: "General"),
            [
                PreviousReview(
                    facet: "General",
                    summary: "General feedback",
                    findings: [Finding(CodeReviewFindingLevel.Comment)]
                ),
            ]
        );

        await Assert.That(section).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_CompletelyDifferentFacet_NoMatch()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            Reviewer(facet: "Performance", displayName: "Performance Reviewer"),
            [
                PreviousReview(
                    facet: "Security",
                    findings: [Finding(CodeReviewFindingLevel.Critical)]
                ),
            ]
        );

        await Assert.That(section).IsEqualTo(string.Empty);
    }
}
