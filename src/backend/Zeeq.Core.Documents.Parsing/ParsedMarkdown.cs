namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// Fully parsed and materialized markdown document. Immutable; safe to hold, cache, and serialize.
/// All string values are detached from parse buffers — no lifetime constraints, no disposal required.
/// </summary>
/// <remarks>
/// Produced by <see cref="MarkdownParser.Parse"/>. The parser runs a single forward pass over the
/// source string using span-based zero-allocation techniques internally; this record is the
/// fully-materialized result returned to callers.
/// </remarks>
/// <param name="Title">
/// Resolved title: front-matter <c>title</c> field → first H1 heading → file name
/// (without extension). Never null or empty when a file name is supplied to the parser.
/// </param>
/// <param name="Keywords">
/// Keywords / tags from the front-matter <c>keywords</c> or <c>tags</c> field.
/// Values are as-authored (not normalized). Empty list when the field is absent.
/// </param>
/// <param name="Headings">
/// Plain heading text, in document order, with no <c>#</c> markers and no level indicator.
/// Example: <c>["Root Header", "Installation", "Advanced Usage"]</c>.
/// As-authored (original case).
/// </param>
/// <param name="Content">
/// The body of the document after the front-matter fence. Includes headings, paragraphs,
/// and code fences. Used as the <c>content</c> column and the input for token counting.
/// </param>
/// <param name="FrontMatter">
/// The raw front-matter block (between the opening and closing <c>---</c> fences), excluding
/// the fence lines themselves. Empty string when no front-matter is present.
/// </param>
/// <param name="Sections">
/// Text sections between headings. Each section carries its owning heading text, the
/// hierarchical heading path, and the section body text.
/// </param>
/// <param name="Snippets">
/// Fenced code blocks with their resolved tags.
/// </param>
public sealed record ParsedMarkdown(
    string Title,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Headings,
    string Content,
    string FrontMatter,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<Snippet> Snippets
);
