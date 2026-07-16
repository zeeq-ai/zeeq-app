namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// A section of body text under a heading in a parsed markdown document.
/// </summary>
/// <remarks>
/// Produced by <see cref="MarkdownParser"/>. Body is the joined text of lines between
/// the owning heading and the next heading (exclusive), preserving newlines.
/// </remarks>
/// <param name="Header">The text of the heading that opens this section (no <c>#</c> markers).</param>
/// <param name="HeadingPath">
/// Hierarchical path from root to this heading, joined with <c>" > "</c>.
/// Example: <c>"Guide > Installation > Linux"</c>.
/// </param>
/// <param name="Body">The body text of the section (paragraphs, non-code lines).</param>
public sealed record Section(string Header, string HeadingPath, string Body);
