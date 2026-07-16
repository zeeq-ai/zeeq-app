namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders the timestamp and optional provenance link at the bottom of the comment.
/// </summary>
public sealed class PullRequestFooterSectionRenderer(ICodeReviewRuntimeStatistics runtimeStatistics)
    : IGitHubCommentSectionRenderer
{
    /// <inheritdoc />
    public string SectionKind => GitHubCommentMarkers.PullRequestFooter;

    /// <inheritdoc />
    public GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    )
    {
        return new(
            SectionKind,
            OrderKey: null,
            GitHubCommentPatchMode.ReplaceSection,
            RenderFooter(context, runtimeStatistics.GetSnapshot())
        );
    }

    private static string RenderFooter(
        CodeReviewCommentRenderContext context,
        CodeReviewRuntimePercentilesSnapshot runtimeSnapshot
    )
    {
        var renderedAt = GitHubCommentMarkdown.FormatUtc(context.RenderedAtUtc);
        var runtimeLine = RenderRuntimeLine(runtimeSnapshot);
        var viewReviewLine = context.ActionLinks.ViewReviewUrl is { } viewUrl
            ? $"[View this review in Zeeq]({viewUrl})\n\n"
            : string.Empty;

        if (string.IsNullOrWhiteSpace(context.ActionLinks.ProvenanceUrl))
        {
            return $"""
                {viewReviewLine}*(Updated at {renderedAt} UTC)*

                {runtimeLine}
                """;
        }

        return $"""
            {viewReviewLine}*(Updated at {renderedAt} UTC)*

            {runtimeLine}

            ---

            [Manage provenance]({context.ActionLinks.ProvenanceUrl})
            """;
    }

    private static string RenderRuntimeLine(CodeReviewRuntimePercentilesSnapshot snapshot)
    {
        if (!snapshot.HasData)
        {
            return "<sub>50th percentile runtime: (no data); 95th: (no data)</sub>";
        }

        return $"<sub>50th percentile runtime: {FormatRuntime(snapshot.Percentile50!.Value)}; 95th: {FormatRuntime(snapshot.Percentile95!.Value)}</sub>";
    }

    private static string FormatRuntime(TimeSpan runtime)
    {
        var totalMinutes = (int)Math.Floor(runtime.TotalMinutes);

        return $"{totalMinutes:00}:{runtime.Seconds:00}";
    }
}
