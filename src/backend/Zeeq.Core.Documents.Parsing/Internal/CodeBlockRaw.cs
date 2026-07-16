namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// A fenced code block extracted from a parsed markdown document.
/// </summary>
/// <param name="Header">The text of the heading above this snippet (no <c>#</c> markers).</param>
/// <param name="HeadingPath">Hierarchical path to the owning heading, joined with <c>" > "</c>.</param>
/// <param name="Preceding">Lines of text between the owning heading and the opening code fence.</param>
/// <param name="Language">The language identifier from the opening fence (e.g. <c>"cs"</c>, <c>"sql"</c>).</param>
/// <param name="Content">The content inside the fence (excludes the fence lines themselves).</param>
/// <param name="Tag">
/// Resolved tag for this snippet. Resolution order:
/// 1. XML fence tag if the block is wrapped.
/// 2. First-line comment tag.
/// 3. Empty string.
/// </param>
internal readonly record struct CodeBlockRaw(
    ReadOnlyMemory<char> Header,
    ReadOnlyMemory<char> HeadingPath,
    ReadOnlyMemory<char> Preceding,
    ReadOnlyMemory<char> Language,
    ReadOnlyMemory<char> Content,
    ReadOnlyMemory<char> Tag
);
