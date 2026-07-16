namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// The kind of content a snippet carries.
/// </summary>
/// <remarks>
/// Snippets are composed from a <see cref="Parsing.ParsedMarkdown"/>: prose
/// <see cref="Section"/> bodies become <see cref="Section"/> snippets and fenced code
/// blocks become <see cref="Code"/> snippets. Search is always scoped to a single kind
/// (the <c>search_sections</c> and <c>search_code_snippets</c> tools map 1:1 to these).
/// </remarks>
public enum SnippetKind
{
    /// <summary>
    /// A prose section body under a heading (excludes fenced code).
    /// </summary>
    Section,

    /// <summary>
    /// A fenced code block with its language, tag, and preceding context.
    /// </summary>
    Code,
}
