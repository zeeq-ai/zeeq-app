namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders signed action links such as requesting another review.
/// </summary>
public sealed class PullRequestActionsSectionRenderer : IGitHubCommentSectionRenderer
{
    /// <inheritdoc />
    public string SectionKind => GitHubCommentMarkers.PullRequestActions;

    /// <inheritdoc />
    public GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    )
    {
        if (string.IsNullOrWhiteSpace(context.ActionLinks.RequestReviewUrl))
        {
            return Remove();
        }

        var label =
            kind == GitHubCommentKinds.DraftPrompt
                ? "Request a Zeeq review"
                : "Request another Zeeq review";

        return new(
            SectionKind,
            OrderKey: null,
            GitHubCommentPatchMode.ReplaceSection,
            $"""
            ----

            [{label}]({context.ActionLinks.RequestReviewUrl})
            """
        );
    }

    private GitHubCommentDomPatch Remove() =>
        new(SectionKind, OrderKey: null, GitHubCommentPatchMode.RemoveSection, Markdown: null);
}
