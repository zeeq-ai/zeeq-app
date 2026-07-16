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
            """
            > 👋 Hi there; I'm an automated review agent connected to Zeeq's docs and memories.

            ----
            """
        );
}
