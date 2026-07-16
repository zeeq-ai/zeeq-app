namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// A declarative update to one section of a GitHub comment DOM.
/// </summary>
/// <remarks>
/// Patches are applied after message-level clear operations. A null order key
/// preserves the existing section rank or falls back to the first-party default
/// rank for known Zeeq sections.
/// </remarks>
public sealed record GitHubCommentDomPatch(
    string SectionKind,
    string? OrderKey,
    GitHubCommentPatchMode Mode,
    string? Markdown
);
