namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Produces a section-level DOM patch for a GitHub comment render kind.
/// </summary>
/// <remarks>
/// Section renderers own narrow slices of the comment. The document renderer
/// preserves all sections that no renderer patches in the current pass.
/// </remarks>
public interface IGitHubCommentSectionRenderer
{
    /// <summary>Stable marker for the section owned by this renderer.</summary>
    string SectionKind { get; }

    /// <summary>
    /// Creates a patch for this renderer's section.
    /// </summary>
    /// <param name="kind">Message kind driving the render, such as <c>queued</c>.</param>
    /// <param name="context">Hydrated review/artifact/action context.</param>
    /// <param name="currentDom">DOM after message-level clear operations have been applied.</param>
    /// <returns>A patch when this renderer should change its section; otherwise <c>null</c>.</returns>
    GitHubCommentDomPatch? Render(
        string kind,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    );
}
