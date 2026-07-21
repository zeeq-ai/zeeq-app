namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders the stable greeting at the top of Zeeq-owned PR comments.
/// </summary>
public sealed class PullRequestHeaderSectionRenderer : IGitHubCommentSectionRenderer
{
    /// <inheritdoc />
    public string SectionKind => GitHubCommentMarkers.PullRequestHeader;

    /// <inheritdoc />
    public GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    ) =>
        new(
            SectionKind,
            OrderKey: null,
            GitHubCommentPatchMode.ReplaceSection,
            RenderHeader(context)
        );

    private static string RenderHeader(CodeReviewCommentRenderContext context)
    {
        var manageReviewLine = context.ActionLinks.ViewReviewUrl is { } viewUrl
            ? $"\n> [Manage this PR in Zeeq]({viewUrl})\n"
            : string.Empty;

        return $"""
            > 👋 Hi there; I'm Zeeq. Start your comment with `/zeeq` or `+zeeq` to leave instructions for followup reviews.{manageReviewLine}

            ----
            """;
    }
}
