namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// Parses a markdown string into a fully-materialized <see cref="ParsedMarkdown"/> result.
/// </summary>
/// <remarks>
/// Internally uses a single forward-pass span/ArrayPool parser. All pooled resources are
/// returned before this method returns — callers have no lifetime or disposal obligations.
///
/// Flow: source → <see cref="ZeeqDocumentParser"/> (internal) → materialize → return
/// </remarks>
public static class MarkdownParser
{
    /// <summary>
    /// Parses <paramref name="source"/> and returns a fully-materialized document.
    /// </summary>
    /// <param name="source">The full markdown string to parse.</param>
    /// <param name="fileName">
    /// File name without extension (e.g. <c>"my-doc"</c>), used as the fallback title when
    /// neither a front-matter <c>title</c> field nor an H1 heading is present.
    /// Pass empty string to suppress file-name fallback.
    /// </param>
    public static ParsedMarkdown Parse(string source, string fileName)
    {
        var parser = ZeeqDocumentParser.Parse(source);
        try
        {
            var title = ResolveTitle(parser, fileName);

            var keywords = new List<string>();
            foreach (var kw in parser.Keywords)
                keywords.Add(kw.ToString());

            var sections = new List<Section>(parser.TextBlocks.Length);
            foreach (var tb in parser.TextBlocks)
            {
                sections.Add(
                    new Section(
                        Header: tb.Header.ToString(),
                        HeadingPath: tb.HeadingPath.ToString(),
                        Body: tb.Body.ToString()
                    )
                );
            }

            var snippets = new List<Snippet>(parser.CodeBlocks.Length);
            foreach (var cb in parser.CodeBlocks)
            {
                snippets.Add(
                    new Snippet(
                        Header: cb.Header.ToString(),
                        HeadingPath: cb.HeadingPath.ToString(),
                        Preceding: cb.Preceding.ToString(),
                        Language: cb.Language.ToString(),
                        Content: cb.Content.ToString(),
                        Tag: cb.Tag.ToString()
                    )
                );
            }

            var contentStart = parser.ContentStart;
            var content =
                contentStart > 0 && contentStart < source.Length ? source[contentStart..] : source;

            return new ParsedMarkdown(
                Title: title,
                Keywords: keywords.AsReadOnly(),
                Headings: parser.Headings,
                Content: content,
                FrontMatter: parser.FrontMatter.ToString(),
                Sections: sections.AsReadOnly(),
                Snippets: snippets.AsReadOnly()
            );
        }
        finally
        {
            parser.Dispose();
        }
    }

    private static string ResolveTitle(ZeeqDocumentParser parser, string fileName)
    {
        var title = parser.Title;
        if (!title.IsEmpty)
            return title.ToString();

        return fileName;
    }
}
