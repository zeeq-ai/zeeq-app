namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Renders a complete GitHub comment body from a current DOM and section patches.
/// </summary>
/// <remarks>
/// The renderer is pure string transformation: it does not call GitHub or the
/// database. The write handler owns serialization and I/O around this operation.
/// </remarks>
public interface IGitHubCommentDomRenderer
{
    /// <summary>
    /// Renders a full comment body while preserving unpatched DOM sections.
    /// </summary>
    /// <param name="kind">Message kind driving the render.</param>
    /// <param name="clear">Section markers to remove before renderer patches run.</param>
    /// <param name="context">Hydrated review/artifact/action context for section renderers.</param>
    /// <param name="currentDom">Current DOM parsed from GitHub.</param>
    /// <returns>Markdown body with Zeeq structural markers.</returns>
    string Render(
        string kind,
        IReadOnlyList<string> clear,
        CodeReviewCommentRenderContext context,
        GitHubCommentDom currentDom
    );
}
