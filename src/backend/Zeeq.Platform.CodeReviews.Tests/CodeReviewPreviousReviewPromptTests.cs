using System.Xml.Linq;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests for CodeReviewAgentExecutor prompt-building helpers:
/// BuildPreviousReviewsSection.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewPreviousReviewPromptTests/*"
/// </summary>
public sealed class CodeReviewPreviousReviewPromptTests
{
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
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection([]);

        await Assert.That(section).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_ReviewWithZeroFindings_ReturnsEmpty()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            [PreviousReview(facet: "Security", findings: [])]
        );

        await Assert.That(section).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildPreviousReviewsSection_MultipleReviews_AggregatesXmlWithFacets()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
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

        var doc = XElement.Parse(section);
        var reviews = doc.Elements("review").ToArray();

        await Assert.That(reviews).Count().IsEqualTo(2);
        await Assert.That(reviews[0].Attribute("facet")!.Value).IsEqualTo("Security");
        await Assert.That(reviews[0].Element("summary")!.Value).IsEqualTo("Review 1");
        await Assert.That(reviews[1].Attribute("facet")!.Value).IsEqualTo("Security");
        await Assert.That(reviews[1].Element("summary")!.Value).IsEqualTo("Review 2");
    }

    [Test]
    public async Task BuildPreviousReviewsSection_IncludesFindingsFromAllFacets()
    {
        var section = CodeReviewAgentExecutor.BuildPreviousReviewsSection(
            [
                PreviousReview(
                    facet: "Performance",
                    summary: "Perf review",
                    findings: [Finding(CodeReviewFindingLevel.Major, summary: "Perf issue")]
                ),
                PreviousReview(
                    facet: "Security",
                    summary: "Security review",
                    findings: [Finding(CodeReviewFindingLevel.Critical, summary: "Security issue")]
                ),
            ]
        );

        var doc = XElement.Parse(section);
        var reviews = doc.Elements("review").ToArray();
        var findings = reviews.SelectMany(review => review.Element("findings")!.Elements()).ToArray();

        await Assert.That(reviews.Select(review => review.Attribute("facet")!.Value))
            .IsEquivalentTo(["Performance", "Security"]);
        await Assert.That(findings.Select(finding => finding.Element("summary")!.Value))
            .IsEquivalentTo(["Perf issue", "Security issue"]);
    }
}
