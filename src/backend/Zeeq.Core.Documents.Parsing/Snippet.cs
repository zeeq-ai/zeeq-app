namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// A fenced code block extracted from a parsed markdown document.
/// </summary>
/// <remarks>
/// The <see cref="Tag"/> is resolved in order: XML fence tag → first-line comment → empty.
/// See <see cref="MarkdownParser"/> for tag resolution rules.
/// </remarks>
/// <param name="Header">The text of the heading above this snippet (no <c>#</c> markers).</param>
/// <param name="HeadingPath">Hierarchical path to the owning heading, joined with <c>" > "</c>.</param>
/// <param name="Preceding">Lines of text between the owning heading and the opening code fence.</param>
/// <param name="Language">The language identifier from the opening fence (e.g. <c>"cs"</c>, <c>"sql"</c>).</param>
/// <param name="Content">The content inside the fence (excludes the fence lines themselves).</param>
/// <param name="Tag">
/// Resolved tag for this snippet. Resolution order:
/// 1. XML fence tag (<c>&lt;tag&gt;…&lt;/tag&gt;</c>) if the block is wrapped.
/// 2. First non-empty content line if it begins with a known comment token (stripped + trimmed).
/// 3. Empty string.
/// </param>
public sealed record Snippet(
    string Header,
    string HeadingPath,
    string Preceding,
    string Language,
    string Content,
    string Tag
);
