namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// One direct child section in a Zeeq-rendered GitHub comment DOM.
/// </summary>
/// <remarks>
/// The DOM supports exactly one level below the root. Section content is kept as
/// raw Markdown so future renderers can preserve sections they do not own.
/// </remarks>
public sealed record GitHubCommentDomSection
{
    /// <summary>Stable section marker, for example <c>zeeq:pr-findings</c>.</summary>
    public required string Marker { get; init; }

    /// <summary>Sortable rank that controls where the section appears.</summary>
    public required string OrderKey { get; init; }

    /// <summary>Raw Markdown content between the section start and end markers.</summary>
    public required string Content { get; init; }

    /// <summary>True when the section body contains non-whitespace content.</summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
}
