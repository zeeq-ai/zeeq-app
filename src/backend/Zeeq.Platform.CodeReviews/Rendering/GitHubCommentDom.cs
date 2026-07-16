namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// One-level document object model parsed from a Zeeq-managed GitHub comment.
/// </summary>
/// <remarks>
/// The live GitHub comment is the durable document state. This type keeps only
/// the target selector, root marker, and ordered child sections so renderers can
/// update their own sections without deleting content owned by other renderers.
/// </remarks>
public sealed class GitHubCommentDom
{
    private readonly IReadOnlyList<GitHubCommentDomSection> _sections;

    /// <summary>
    /// Creates a DOM from already ordered sections.
    /// </summary>
    /// <param name="target">Logical GitHub comment target.</param>
    /// <param name="rootMarker">Root marker found in the comment body.</param>
    /// <param name="sections">Direct child sections in document order.</param>
    public GitHubCommentDom(
        GitHubCommentTargetSelector target,
        string rootMarker,
        IReadOnlyList<GitHubCommentDomSection> sections
    )
    {
        Target = target;
        RootMarker = rootMarker;
        _sections = sections;
    }

    /// <summary>Logical GitHub comment target represented by this DOM.</summary>
    public GitHubCommentTargetSelector Target { get; }

    /// <summary>Root marker for this comment document.</summary>
    public string RootMarker { get; }

    /// <summary>Direct child sections in document order.</summary>
    public IReadOnlyList<GitHubCommentDomSection> Sections => _sections;

    /// <summary>True when the DOM has no child sections yet.</summary>
    public bool IsEmpty => _sections.Count == 0;

    /// <summary>
    /// Creates an empty DOM for a target that has not yet been written to GitHub.
    /// </summary>
    /// <param name="target">Logical GitHub comment target.</param>
    /// <returns>An empty DOM with the target's default root marker.</returns>
    public static GitHubCommentDom Empty(GitHubCommentTargetSelector target) =>
        new(target, GitHubCommentMarkers.RootFor(target), []);

    /// <summary>
    /// Finds a section by marker.
    /// </summary>
    /// <param name="marker">Stable section marker.</param>
    /// <returns>The matching section, or <c>null</c> when absent.</returns>
    public GitHubCommentDomSection? FindSection(string marker) =>
        _sections.FirstOrDefault(section => section.Marker == marker);

    /// <summary>
    /// Returns a new DOM with a different ordered section list.
    /// </summary>
    /// <param name="sections">Replacement section list, already in desired document order.</param>
    /// <returns>A DOM with the same target and root marker.</returns>
    public GitHubCommentDom WithSections(IReadOnlyList<GitHubCommentDomSection> sections) =>
        new(Target, RootMarker, sections);
}
