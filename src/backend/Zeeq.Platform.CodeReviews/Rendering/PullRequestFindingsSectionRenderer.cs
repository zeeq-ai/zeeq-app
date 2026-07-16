using System.Globalization;
using System.Text;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders completed review facets, findings, and the V1-style totals table.
/// </summary>
public sealed class PullRequestFindingsSectionRenderer : IGitHubCommentSectionRenderer
{
    /// <inheritdoc />
    public string SectionKind => GitHubCommentMarkers.PullRequestFindings;

    /// <inheritdoc />
    public GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    )
    {
        if (kind is not ("review_completed" or "stub_review_completed" or "no_agents_activated"))
        {
            return null;
        }

        if (context.FindingsLoadError is not null)
        {
            return Remove();
        }

        if (context.Findings is null)
        {
            return Remove();
        }

        if (context.Findings.NoAgentsActivated)
        {
            return Replace(
                """
                > [!NOTE]
                > No configured code review agents activated for this pull request. The changed files did not match any agent activation filters or all agents were skipped by the filters.
                """
            );
        }

        var markdown = RenderFindings(context.Findings, context);

        return string.IsNullOrWhiteSpace(markdown) ? Remove() : Replace(markdown);
    }

    private static string RenderFindings(
        CodeReviewOutputDocument findings,
        CodeReviewCommentRenderContext context
    )
    {
        var sb = new StringBuilder();
        var renderedFacets = findings.Reviews.Where(review => review.Findings.Count > 0).ToArray();
        var totalFindings = renderedFacets.Sum(review => review.Findings.Count);

        if (totalFindings == 0)
        {
            return RenderNoFindings(findings, context);
        }

        foreach (var review in renderedFacets)
        {
            AppendFacet(sb, review);
        }

        if (renderedFacets.Length > 0)
        {
            sb.AppendLine("----");
            sb.AppendLine();
        }

        AppendTotalsTable(sb, renderedFacets);

        return sb.ToString().TrimEnd();
    }

    private static string RenderNoFindings(
        CodeReviewOutputDocument findings,
        CodeReviewCommentRenderContext context
    )
    {
        var reviewerCount = findings.Reviews.Count;
        var noice = RenderNoice(context);

        return $"""
            > [!TIP]
            > 😎 Nice! That's one clean PR ✨. Zero findings from the {FormatReviewerCount(
                reviewerCount
            )} participating on this PR.

            {noice}
            """.TrimEnd();
    }

    private static string RenderNoice(CodeReviewCommentRenderContext context)
    {
        if (!context.ShowNoice || string.IsNullOrWhiteSpace(context.ActionLinks.NoiceImageUrl))
        {
            return string.Empty;
        }

        return $"""
            <details>
            <summary>❮EXPAND❯ Noice!</summary>
            <p align="center"><img src="{context.ActionLinks.NoiceImageUrl}" /></p>
            </details>
            """;
    }

    private static void AppendFacet(StringBuilder sb, CodeReviewFacetOutput review)
    {
        var facet = GitHubCommentMarkdown.SanitizeModelMarkdown(review.Facet).ToUpperInvariant();
        sb.AppendLine($"## {facet} REVIEW ({FormatFindingCount(review.Findings.Count)})");
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine();
        sb.AppendLine(
            $"<summary>❮EXPAND❯ {GitHubCommentMarkdown.EncodeSummary(review.Summary)}</summary>"
        );
        sb.AppendLine();

        var details = GitHubCommentMarkdown.Blockquote(review.Details);
        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.AppendLine(details);
            sb.AppendLine();
        }

        foreach (var finding in review.Findings)
        {
            sb.AppendLine();
            sb.AppendLine("----");
            sb.AppendLine();
            AppendFinding(sb, finding);
        }

        sb.AppendLine("</details>");
        sb.AppendLine();
        sb.AppendLine();
    }

    private static void AppendFinding(StringBuilder sb, CodeReviewFindingOutput finding)
    {
        var level = finding.Level.ToString().ToUpperInvariant();
        var summary = GitHubCommentMarkdown.SanitizeModelMarkdown(finding.Summary);
        sb.AppendLine($"### 👉 «{level}» {summary}");
        sb.AppendLine();
        sb.AppendLine(RenderLocation(finding));
        sb.AppendLine();

        var body = GitHubCommentMarkdown.SanitizeModelMarkdown(finding.Details);
        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.AppendLine(body);
            sb.AppendLine();
        }
    }

    private static string RenderLocation(CodeReviewFindingOutput finding)
    {
        var parts = new List<string>
        {
            $"File: `{GitHubCommentMarkdown.SanitizeModelMarkdown(finding.File)}`",
        };
        if (finding.Line > 0)
        {
            parts.Add($"Line: {finding.Line.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(finding.Side))
        {
            parts.Add($"Side: {GitHubCommentMarkdown.SanitizeModelMarkdown(finding.Side)}");
        }

        return $"({string.Join(", ", parts)})";
    }

    private static void AppendTotalsTable(
        StringBuilder sb,
        IReadOnlyList<CodeReviewFacetOutput> reviews
    )
    {
        sb.AppendLine("|Facet / Level|‼️Critical|⚠️Major|Minor|Suggestion|Comment|");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");

        var totals = new FindingCounts();
        foreach (var review in reviews)
        {
            var counts = FindingCounts.From(review.Findings);
            totals += counts;
            sb.AppendLine(
                $"|{GitHubCommentMarkdown.SanitizeModelMarkdown(review.Facet)}|{counts.Critical}|{counts.Major}|{counts.Minor}|{counts.Suggestion}|{counts.Comment}|"
            );
        }

        sb.AppendLine(
            $"|***TOTAL***|***{totals.Critical}***|***{totals.Major}***|***{totals.Minor}***|***{totals.Suggestion}***|***{totals.Comment}***|"
        );

        AppendSeverityAdmonitions(sb, totals);
    }

    private static void AppendSeverityAdmonitions(StringBuilder sb, FindingCounts totals)
    {
        if (totals.Critical > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"> [!CAUTION]\n> Encountered {totals.Critical.ToString(CultureInfo.InvariantCulture)} critical {FormatFindingWord(totals.Critical)} in this PR that should be reviewed; ship only if you are certain."
            );
        }

        if (totals.Major > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"> [!WARNING]\n> Encountered {totals.Major.ToString(CultureInfo.InvariantCulture)} major {FormatFindingWord(totals.Major)} in this PR that should be reviewed; check to see if these are valid.  Ship if these are expected; consider adding comments to the code to explain to future travelers (agents) why."
            );
        }
    }

    private static string FormatFindingCount(int count) =>
        count == 1 ? "1 finding" : $"{count.ToString(CultureInfo.InvariantCulture)} findings";

    private static string FormatFindingWord(int count) => count == 1 ? "finding" : "findings";

    private static string FormatReviewerCount(int count) =>
        count == 1 ? "1 reviewer" : $"{count.ToString(CultureInfo.InvariantCulture)} reviewers";

    private GitHubCommentDomPatch Replace(string markdown) =>
        new(SectionKind, OrderKey: null, GitHubCommentPatchMode.ReplaceSection, markdown);

    private GitHubCommentDomPatch Remove() =>
        new(SectionKind, OrderKey: null, GitHubCommentPatchMode.RemoveSection, Markdown: null);

    private readonly record struct FindingCounts(
        int Critical,
        int Major,
        int Minor,
        int Suggestion,
        int Comment
    )
    {
        public static FindingCounts From(IReadOnlyList<CodeReviewFindingOutput> findings)
        {
            var counts = new FindingCounts();
            foreach (var finding in findings)
            {
                counts = finding.Level switch
                {
                    CodeReviewFindingLevel.Critical => counts with
                    {
                        Critical = counts.Critical + 1,
                    },
                    CodeReviewFindingLevel.Major => counts with { Major = counts.Major + 1 },
                    CodeReviewFindingLevel.Minor => counts with { Minor = counts.Minor + 1 },
                    CodeReviewFindingLevel.Suggestion => counts with
                    {
                        Suggestion = counts.Suggestion + 1,
                    },
                    CodeReviewFindingLevel.Comment => counts with { Comment = counts.Comment + 1 },
                    _ => counts,
                };
            }

            return counts;
        }

        public static FindingCounts operator +(FindingCounts left, FindingCounts right) =>
            new(
                left.Critical + right.Critical,
                left.Major + right.Major,
                left.Minor + right.Minor,
                left.Suggestion + right.Suggestion,
                left.Comment + right.Comment
            );
    }
}
