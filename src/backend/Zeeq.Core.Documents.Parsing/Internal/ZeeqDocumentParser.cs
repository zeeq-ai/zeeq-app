using System.Buffers;

namespace Zeeq.Core.Documents.Parsing;

/// <summary>
/// Zero-allocation, single-pass markdown document parser.
/// </summary>
/// <remarks>
/// Processes the source string line-by-line using a four-state machine
/// (Normal → AfterXmlOpen → InCodeFence → AfterCodeClose).
/// All block arrays are rented from <see cref="ArrayPool{T}.Shared"/> to avoid
/// per-document heap allocations — call <see cref="Dispose"/> to return them.
///
/// Offsets into the original source string (<see cref="SourceRange"/>) are kept
/// throughout; strings are only materialised on demand by the public API.
/// </remarks>
internal struct ZeeqDocumentParser : IDisposable
{
    private readonly string _src;

    // Front matter
    private readonly SourceRange _fm;
    private readonly SourceRange _title;
    private readonly SourceRange[] _kw;
    private readonly int _contentStart;

    // Blocks (rented from ArrayPool)
    private TextBlockRaw[] _textArr;
    private int _textCount;
    private CodeBlockRaw[] _codeArr;
    private int _codeCount;

    // Path strings and headings
    private string[] _paths;
    private string[] _headings;

    private bool _disposed;

    private ZeeqDocumentParser(
        string source,
        SourceRange fm,
        SourceRange title,
        SourceRange[] kw,
        TextBlockRaw[] textArr,
        int textCount,
        CodeBlockRaw[] codeArr,
        int codeCount,
        string[] paths,
        string[] headings,
        int contentStart
    )
    {
        _src = source;
        _fm = fm;
        _title = title;
        _kw = kw;
        _textArr = textArr;
        _textCount = textCount;
        _codeArr = codeArr;
        _codeCount = codeCount;
        _paths = paths;
        _headings = headings;
        _contentStart = contentStart;
        _disposed = false;
    }

    // ── Public API ─────────────────────────────────────────────

    /// <summary>The resolved title: front-matter <c>title:</c> field, first H1 heading, or empty.</summary>
    public ReadOnlyMemory<char> Title => _title.Materialize(_src).AsMemory();

    /// <summary>The raw text between the front-matter <c>---</c> fences, or empty when absent.</summary>
    public ReadOnlyMemory<char> FrontMatter => _fm.Materialize(_src).AsMemory();

    /// <summary>Text sections, one per heading block (including pre-heading content with empty header).</summary>
    public ReadOnlySpan<TextBlockRaw> TextBlocks => _textArr.AsSpan(0, _textCount);

    /// <summary>Fenced code blocks extracted from the document body.</summary>
    public ReadOnlySpan<CodeBlockRaw> CodeBlocks => _codeArr.AsSpan(0, _codeCount);

    /// <summary>Position in source where body content starts (after the front-matter closing fence).</summary>
    public int ContentStart => _contentStart;

    /// <summary>Keywords / tags declared in the front-matter.</summary>
    public KeywordEnumerable Keywords => new(_src, _kw);

    /// <summary>Flat list of plain heading texts in document order.</summary>
    public IReadOnlyList<string> Headings => _headings;

    /// <summary>Returns the rented arrays to the pool. Must be called exactly once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_textArr is not null)
            ArrayPool<TextBlockRaw>.Shared.Return(_textArr);

        if (_codeArr is not null)
            ArrayPool<CodeBlockRaw>.Shared.Return(_codeArr);

        _textArr = null!;
        _codeArr = null!;
    }

    // ── Parse ──────────────────────────────────────────────────

    /// <summary>Parses a markdown string and returns the parsed document.</summary>
    public static ZeeqDocumentParser Parse(string markdown)
    {
        int pos = 0,
            len = markdown.Length;

        // ---- Phase 1: front matter ----

        var frontMatter = new SourceRange(0, 0);
        var title = new SourceRange(0, 0);
        var keywords = new List<SourceRange>();

        if (pos < len && MatchFence(markdown, ref pos, '-'))
        {
            int fmStart = pos; // pos is already past the opening ---

            while (pos < len)
            {
                int lineStart = pos;
                int lineEnd = SkipToNextLine(markdown, pos);
                pos = AdvancePastLineEnding(markdown, lineEnd);

                if (lineStart >= lineEnd)
                    continue;

                var line = markdown.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');

                if (IsFenceLine(line, '-'))
                {
                    frontMatter = new SourceRange(fmStart, lineStart);
                    break;
                }

                // Unclosed front-matter fence: treat the first heading as body content.
                if (!line.IsEmpty && line[0] == '#')
                {
                    pos = lineStart;
                    break;
                }

                // Parse YAML key: value fields.
                int colon = line.IndexOf(':');
                if (colon < 0)
                    continue;

                var key = line[..colon].Trim();
                int valStart = lineStart + colon + 1,
                    valEnd = lineEnd;

                while (valStart < valEnd && IsSpace(markdown[valStart]))
                    valStart++;
                while (valEnd > valStart && IsSpace(markdown[valEnd - 1]))
                    valEnd--;

                if (key.Equals("Title", StringComparison.OrdinalIgnoreCase))
                {
                    title = new SourceRange(valStart, valEnd);
                }
                else if (
                    key.Equals("Tags", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Keywords", StringComparison.OrdinalIgnoreCase)
                )
                {
                    ParseKeywords(
                        markdown,
                        lineStart,
                        lineEnd,
                        valStart,
                        valEnd,
                        keywords,
                        ref pos
                    );
                }
            }
        }
        else
            pos = 0; // no front matter

        int contentStart = pos;

        // ---- Phase 2: body ----

        const int InitialCapacity = 16;
        var textBlockPool = ArrayPool<TextBlockRaw>.Shared;
        var codeBlockPool = ArrayPool<CodeBlockRaw>.Shared;
        var textBlocks = textBlockPool.Rent(InitialCapacity);
        var codeBlocks = codeBlockPool.Rent(InitialCapacity);
        try
        {

        int textCount = 0,
            codeCount = 0;

        var pathList = new List<string>();
        var headingsList = new List<string>();

        // Heading stack: tracks ancestor headings for breadcrumb path building.
        // Each entry holds (Level, HeadingStart, HeadingEnd, PathIndex) into the source string.
        var headingStack =
            new Stack<(int Level, int HeadingStart, int HeadingEnd, int PathIndex)>();
        var paragraphLines = new List<(int Start, int End)>();
        int headingPathIndex = -1; // -1 until the first heading is seen

        bool hasFenceTag = false;
        int xmlTagStart = 0,
            xmlTagEnd = 0;
        State state = State.Normal;
        var fenceLines = new List<(int Start, int End)>();
        int langStart = 0,
            langEnd = 0,
            fenceHeadingPathIndex = 0;
        var fencePreceding = new List<(int Start, int End)>();

        while (pos < len)
        {
            int lineStart = pos;
            int lineEnd = SkipToNextLine(markdown, pos);
            pos = AdvancePastLineEnding(markdown, lineEnd);

            if (lineStart >= lineEnd)
                continue;

            var line = markdown.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');

            switch (state)
            {
                case State.Normal:
                    if (
                        TryHeading(
                            line,
                            lineStart,
                            out int level,
                            out int headingStart,
                            out int headingEnd
                        )
                    )
                    {
                        // Flush accumulated paragraph text before the new heading.
                        if (paragraphLines.Count > 0)
                        {
                            if (textCount == textBlocks.Length)
                                textBlocks = Grow(textBlockPool, textBlocks, textCount);

                            EmitTextBlock(
                                arr: textBlocks,
                                count: ref textCount,
                                src: markdown,
                                hStack: headingStack,
                                paragraphLines: paragraphLines,
                                headingPathIndex: headingPathIndex,
                                paths: pathList
                            );
                        }

                        paragraphLines.Clear();

                        // Pop all headings at the same or deeper level before pushing the new one.
                        while (headingStack.Count > 0 && headingStack.Peek().Level >= level)
                            headingStack.Pop();

                        headingPathIndex = AddPath(
                            markdown,
                            headingStack,
                            level,
                            headingStart,
                            headingEnd,
                            pathList
                        );
                        headingStack.Push((level, headingStart, headingEnd, headingPathIndex));
                        headingsList.Add(markdown[headingStart..headingEnd]);
                    }
                    else if (TryXmlOpen(line, lineStart, out xmlTagStart, out xmlTagEnd))
                    {
                        state = State.AfterXmlOpen;
                    }
                    else if (TryFenceOpen(line, lineStart, out langStart, out langEnd))
                    {
                        hasFenceTag = false;
                        state = State.InCodeFence;
                        fenceLines.Clear();
                        fencePreceding.Clear();
                        fencePreceding.AddRange(paragraphLines);
                        fenceHeadingPathIndex = headingPathIndex;
                    }
                    else
                    {
                        paragraphLines.Add((lineStart, lineEnd));
                    }
                    break;

                case State.AfterXmlOpen:
                    if (TryFenceOpen(line, lineStart, out langStart, out langEnd))
                    {
                        hasFenceTag = true;
                        state = State.InCodeFence;
                        fenceLines.Clear();
                        fencePreceding.Clear();
                        fencePreceding.AddRange(paragraphLines);
                        fenceHeadingPathIndex = headingPathIndex;
                    }
                    else
                    {
                        // Not a fenced snippet — treat the XML tag as plain text and reprocess this line.
                        // NOTE: xmlTagStart/xmlTagEnd were captured in the prior iteration; this adds the
                        // tag line, and the goto re-processes the *current* (different) line — no duplicate.
                        paragraphLines.Add((xmlTagStart - 1, xmlTagEnd + 1));
                        state = State.Normal;
                        goto case State.Normal;
                    }
                    break;

                case State.InCodeFence:
                    if (IsFenceClose(line))
                    {
                        if (hasFenceTag)
                        {
                            state = State.AfterCodeClose;
                        }
                        else
                        {
                            if (codeCount == codeBlocks.Length)
                                codeBlocks = Grow(codeBlockPool, codeBlocks, codeCount);

                            EmitCodeBlock(
                                arr: codeBlocks,
                                count: ref codeCount,
                                src: markdown,
                                hStack: headingStack,
                                paths: pathList,
                                fenceLines: fenceLines,
                                langStart: langStart,
                                langEnd: langEnd,
                                preceding: fencePreceding,
                                headingPathIndex: fenceHeadingPathIndex,
                                fenceTag: ReadOnlyMemory<char>.Empty
                            );
                            state = State.Normal;
                        }
                    }
                    else
                    {
                        fenceLines.Add((lineStart, lineEnd));
                    }
                    break;

                case State.AfterCodeClose:
                    if (TryXmlClose(markdown, line, xmlTagStart, xmlTagEnd))
                    {
                        if (codeCount == codeBlocks.Length)
                            codeBlocks = Grow(codeBlockPool, codeBlocks, codeCount);

                        EmitCodeBlock(
                            arr: codeBlocks,
                            count: ref codeCount,
                            src: markdown,
                            hStack: headingStack,
                            paths: pathList,
                            fenceLines: fenceLines,
                            langStart: langStart,
                            langEnd: langEnd,
                            preceding: fencePreceding,
                            headingPathIndex: fenceHeadingPathIndex,
                            fenceTag: markdown.AsMemory(xmlTagStart, xmlTagEnd - xmlTagStart)
                        );
                        state = State.Normal;
                        hasFenceTag = false;
                    }
                    else
                    {
                        // XML close not found — emit the opening tag as plain text and the code block untagged,
                        // then reprocess this line in Normal state.
                        // NOTE: xmlTagStart/xmlTagEnd are from the open tag captured before the fence; adding
                        // them here does not duplicate the current line (same reasoning as AfterXmlOpen).
                        paragraphLines.Add((xmlTagStart - 1, xmlTagEnd + 1));

                        if (codeCount == codeBlocks.Length)
                            codeBlocks = Grow(codeBlockPool, codeBlocks, codeCount);

                        EmitCodeBlock(
                            arr: codeBlocks,
                            count: ref codeCount,
                            src: markdown,
                            hStack: headingStack,
                            paths: pathList,
                            fenceLines: fenceLines,
                            langStart: langStart,
                            langEnd: langEnd,
                            preceding: fencePreceding,
                            headingPathIndex: fenceHeadingPathIndex,
                            fenceTag: ReadOnlyMemory<char>.Empty
                        );
                        state = State.Normal;
                        hasFenceTag = false;
                        goto case State.Normal;
                    }
                    break;
            }
        }

        // Flush any trailing paragraph text after the last heading or code block.
        if (paragraphLines.Count > 0)
        {
            if (textCount == textBlocks.Length)
                textBlocks = Grow(textBlockPool, textBlocks, textCount);

            EmitTextBlock(
                arr: textBlocks,
                count: ref textCount,
                src: markdown,
                hStack: headingStack,
                paragraphLines: paragraphLines,
                headingPathIndex: headingPathIndex,
                paths: pathList
            );
        }

        // Fall back to the first H1 in the body when no title was declared in the front-matter.
        if (title.IsEmpty)
        {
            int firstH1Start = 0,
                firstH1End = 0;
            FindFirstH1(markdown, ref firstH1Start, ref firstH1End);
            title = new SourceRange(firstH1Start, firstH1End);
        }

        return new ZeeqDocumentParser(
            source: markdown,
            fm: frontMatter,
            title: title,
            kw: keywords.ToArray(),
            textArr: textBlocks,
            textCount: textCount,
            codeArr: codeBlocks,
            codeCount: codeCount,
            paths: pathList.ToArray(),
            headings: headingsList.ToArray(),
            contentStart: contentStart
        );

        } // try
        catch
        {
            textBlockPool.Return(textBlocks);
            codeBlockPool.Return(codeBlocks);
            throw;
        }
    }

    // ── Front-matter keyword parsing ───────────────────────────

    /// <summary>
    /// Parses keywords/tags from a front-matter field value, dispatching to the correct
    /// form based on the value shape.
    /// </summary>
    /// <remarks>
    /// Handles three forms:
    /// <list type="number">
    ///   <item>CSV — <c>keywords: a, b, c</c></item>
    ///   <item>Block list — <c>tags:\n  - a\n  - b</c> (empty value, bullets on following lines)</item>
    ///   <item>Inline array — <c>keywords: [a, b, c]</c></item>
    /// </list>
    /// </remarks>
    private static void ParseKeywords(
        string src,
        int lineStart,
        int lineEnd,
        int valStart,
        int valEnd,
        List<SourceRange> keywords,
        ref int pos
    )
    {
        while (valStart < valEnd && IsSpace(src[valStart]))
            valStart++;
        while (valEnd > valStart && IsSpace(src[valEnd - 1]))
            valEnd--;

        if (valStart >= valEnd)
        {
            // Form 2: Block list — value is empty; expect "  - item" on subsequent lines.
            ParseBlockListKeywords(src, ref pos, keywords);
            return;
        }

        if (src[valStart] == '[')
        {
            // Form 3: Inline array [a, b, c] — strip brackets then treat as CSV.
            valStart++; // skip [
            while (valStart < valEnd && IsSpace(src[valStart]))
                valStart++;

            int bracketEnd = valEnd;
            while (bracketEnd > valStart && src[bracketEnd - 1] == ']')
                bracketEnd--;
            while (bracketEnd > valStart && IsSpace(src[bracketEnd - 1]))
                bracketEnd--;

            SplitCsvKeywords(src, valStart, bracketEnd, keywords);
            return;
        }

        // Form 1: CSV — split on commas.
        SplitCsvKeywords(src, valStart, valEnd, keywords);
    }

    /// <summary>Splits a comma-separated keyword string and appends each trimmed value to <paramref name="keywords"/>.</summary>
    private static void SplitCsvKeywords(string src, int start, int end, List<SourceRange> keywords)
    {
        int pos = start;
        while (pos < end)
        {
            while (pos < end && IsSpace(src[pos]))
                pos++;

            if (pos >= end)
                break;

            int kwStart = pos;
            while (pos < end && src[pos] != ',')
                pos++;

            int kwEnd = pos;
            while (kwEnd > kwStart && IsSpace(src[kwEnd - 1]))
                kwEnd--;

            if (kwEnd > kwStart)
                keywords.Add(new SourceRange(kwStart, kwEnd));

            if (pos < end)
                pos++; // skip comma
        }
    }

    /// <summary>
    /// Reads YAML block-list items (<c>  - value</c>) from the current source position,
    /// stopping at the first line that is not a bullet item.
    /// </summary>
    private static void ParseBlockListKeywords(string src, ref int pos, List<SourceRange> keywords)
    {
        int len = src.Length;
        while (pos < len)
        {
            int lineStart = pos;
            int lineEnd = SkipToNextLine(src, pos);

            // Skip leading whitespace to find the "- " bullet marker.
            int linePos = lineStart;
            while (linePos < lineEnd && (src[linePos] == ' ' || src[linePos] == '\t'))
                linePos++;

            // Not a bullet — leave pos pointing to this line so the outer loop sees it.
            if (linePos >= lineEnd - 1 || src[linePos] != '-' || src[linePos + 1] != ' ')
                break;

            pos = AdvancePastLineEnding(src, lineEnd);

            int valStart = linePos + 2;
            int valEnd = lineEnd;
            while (valStart < valEnd && src[valStart] == ' ')
                valStart++;
            while (valEnd > valStart && IsSpace(src[valEnd - 1]))
                valEnd--;

            if (valEnd > valStart)
                keywords.Add(new SourceRange(valStart, valEnd));
        }
    }

    // ── State machine ──────────────────────────────────────────

    /// <summary>Parser states used by the body scanning loop.</summary>
    private enum State : byte
    {
        /// <summary>Normal body scan — watching for headings, XML open tags, and code fences.</summary>
        Normal,

        /// <summary>
        /// A bare XML open tag was seen on its own line (e.g. <c>&lt;example&gt;</c>).
        /// Waiting for a code fence to confirm this is a tagged snippet, or reverting to Normal.
        /// </summary>
        AfterXmlOpen,

        /// <summary>Inside a fenced code block — accumulating fence lines until the closing fence.</summary>
        InCodeFence,

        /// <summary>
        /// The closing fence was seen for a tagged snippet.
        /// Waiting for the matching XML close tag to finalise the block's tag.
        /// </summary>
        AfterCodeClose,
    }

    // ── Emit helpers ───────────────────────────────────────────

    /// <summary>
    /// Finalises a text block from the accumulated paragraph lines and appends it to the output array.
    /// </summary>
    /// <remarks>
    /// When no heading has been seen yet (<paramref name="hStack"/> is empty), emits with empty
    /// header and path so that pre-heading content is preserved as a root-level section.
    /// Clears <paramref name="paragraphLines"/> after emit.
    /// </remarks>
    private static void EmitTextBlock(
        TextBlockRaw[] arr,
        ref int count,
        string src,
        Stack<(int Level, int HeadingStart, int HeadingEnd, int PathIndex)> hStack,
        List<(int Start, int End)> paragraphLines,
        int headingPathIndex,
        List<string> paths
    )
    {
        var header = ReadOnlyMemory<char>.Empty;
        var path = ReadOnlyMemory<char>.Empty;

        if (hStack.Count > 0)
        {
            var (_, headingStart, headingEnd, _) = hStack.Peek();
            header = src.AsMemory(headingStart, headingEnd - headingStart);
            path =
                headingPathIndex >= 0
                    ? paths[headingPathIndex].AsMemory()
                    : ReadOnlyMemory<char>.Empty;
        }

        arr[count++] = new TextBlockRaw(
            Header: header,
            HeadingPath: path,
            Body: JoinRanges(src, paragraphLines)
        );

        paragraphLines.Clear();
    }

    /// <summary>
    /// Finalises a fenced code block and appends it to the output array.
    /// </summary>
    /// <remarks>
    /// Emits with empty header and path when no heading has been seen yet.
    /// When <paramref name="fenceTag"/> is empty, the tag is inferred from the first comment
    /// line in the fence content via <see cref="ResolveCommentTag"/>.
    /// Clears <paramref name="fenceLines"/> and <paramref name="preceding"/> after emit.
    /// </remarks>
    private static void EmitCodeBlock(
        CodeBlockRaw[] arr,
        ref int count,
        string src,
        Stack<(int Level, int HeadingStart, int HeadingEnd, int PathIndex)> hStack,
        List<string> paths,
        List<(int Start, int End)> fenceLines,
        int langStart,
        int langEnd,
        List<(int Start, int End)> preceding,
        int headingPathIndex,
        ReadOnlyMemory<char> fenceTag
    )
    {
        var header = ReadOnlyMemory<char>.Empty;
        var path = ReadOnlyMemory<char>.Empty;

        if (hStack.Count > 0)
        {
            var (_, headingStart, headingEnd, _) = hStack.Peek();
            header = src.AsMemory(headingStart, headingEnd - headingStart);
            path =
                headingPathIndex >= 0
                    ? paths[headingPathIndex].AsMemory()
                    : ReadOnlyMemory<char>.Empty;
        }

        var tag = fenceTag.IsEmpty ? ResolveCommentTag(src, fenceLines) : fenceTag;

        arr[count++] = new CodeBlockRaw(
            Header: header,
            HeadingPath: path,
            Preceding: JoinRanges(src, preceding),
            Language: langStart < langEnd ? src.AsMemory(langStart, langEnd - langStart) : default,
            Content: JoinRanges(src, fenceLines),
            Tag: tag
        );

        fenceLines.Clear();
        preceding.Clear();
    }

    // ── Comment tag resolution ─────────────────────────────────

    /// <summary>
    /// Infers a snippet tag by stripping a recognised comment prefix from the first non-empty
    /// line of a code block (<c>//</c>, <c>#</c>, <c>--</c>, <c>/*…*/</c>, <c>&lt;!--…--&gt;</c>).
    /// </summary>
    /// <remarks>
    /// Returns <see cref="ReadOnlyMemory{T}.Empty"/> when no comment tag is found.
    /// Only the first non-empty line is inspected.
    /// </remarks>
    private static ReadOnlyMemory<char> ResolveCommentTag(
        string src,
        List<(int Start, int End)> codeLines
    )
    {
        for (int i = 0; i < codeLines.Count; i++)
        {
            var (lineStart, lineEnd) = codeLines[i];

            bool empty = true;
            for (int linePos = lineStart; linePos < lineEnd; linePos++)
            {
                if (!IsSpace(src[linePos]))
                {
                    empty = false;
                    break;
                }
            }

            if (empty)
                continue;

            var line = src.AsSpan(lineStart, lineEnd - lineStart).TrimStart(" \t\r");

            // Try prefixes longest-first so that `<!--` is not accidentally matched by `//`.
            if (TryStripCommentPrefix(line, "<!--", "-->", out _))
                return ResolveTagFromSpan(src, lineStart, lineEnd, line, "<!--", "-->");
            if (TryStripCommentPrefix(line, "/*", "*/", out _))
                return ResolveTagFromSpan(src, lineStart, lineEnd, line, "/*", "*/");
            if (TryStripCommentPrefix(line, "//", null, out _))
                return ResolveTagFromSpan(src, lineStart, lineEnd, line, "//", null);
            if (TryStripCommentPrefix(line, "--", null, out _))
                return ResolveTagFromSpan(src, lineStart, lineEnd, line, "--", null);
            if (TryStripCommentPrefix(line, "#", null, out _))
                return ResolveTagFromSpan(src, lineStart, lineEnd, line, "#", null);

            break; // Only check the first non-empty line.
        }

        return ReadOnlyMemory<char>.Empty;
    }

    /// <summary>
    /// Returns true when <paramref name="line"/> starts with <paramref name="prefix"/> and
    /// contains non-whitespace content after stripping the prefix and optional <paramref name="closer"/>.
    /// Sets <paramref name="remainder"/> to the trimmed content.
    /// </summary>
    private static bool TryStripCommentPrefix(
        ReadOnlySpan<char> line,
        string prefix,
        string? closer,
        out ReadOnlySpan<char> remainder
    )
    {
        remainder = default;
        if (!line.StartsWith(prefix))
            return false;

        remainder = line[prefix.Length..];

        if (closer is not null)
        {
            int closeIdx = remainder.IndexOf(closer, StringComparison.Ordinal);
            if (closeIdx >= 0)
                remainder = remainder[..closeIdx];
        }

        remainder = remainder.Trim();
        return !remainder.IsEmpty;
    }

    /// <summary>
    /// Returns a <see cref="ReadOnlyMemory{T}"/> slice of <paramref name="src"/> covering the
    /// comment tag content, by mapping the trimmed span offsets back to absolute source positions.
    /// </summary>
    private static ReadOnlyMemory<char> ResolveTagFromSpan(
        string src,
        int lineStart,
        int lineEnd,
        ReadOnlySpan<char> line,
        string prefix,
        string? closer
    )
    {
        var remainder = line[prefix.Length..];

        if (closer is not null)
        {
            int closeIdx = remainder.IndexOf(closer, StringComparison.Ordinal);
            if (closeIdx >= 0)
                remainder = remainder[..closeIdx];
        }

        remainder = remainder.Trim();

        if (remainder.IsEmpty)
            return ReadOnlyMemory<char>.Empty;

        // Map the trimmed span offset back to an absolute position in src.
        // leadingSpaces accounts for the TrimStart applied before this method was called.
        int leadingSpaces = (lineEnd - lineStart) - line.Length;
        var afterPrefix = line[prefix.Length..];
        int contentOffset = afterPrefix.IndexOf(remainder[0]);
        int tagStart = lineStart + leadingSpaces + prefix.Length + contentOffset;

        return src.AsMemory(tagStart, remainder.Length);
    }

    // ── Path builder ───────────────────────────────────────────

    /// <summary>
    /// Builds a breadcrumb heading path from the current <paramref name="stack"/> plus the new heading,
    /// appends it to <paramref name="paths"/>, and returns the index of the new entry.
    /// </summary>
    /// <remarks>
    /// Path format example: <c>"Guide &gt; Installation &gt; Linux"</c>.
    /// The stack is iterated top-to-bottom (most-recent first), so parts are reversed before joining.
    /// Handles skipped heading levels (e.g. H2 → H4 with no H3) by nesting based on relative level,
    /// not absolute depth.
    /// </remarks>
    private static int AddPath(
        string src,
        Stack<(int Level, int HeadingStart, int HeadingEnd, int PathIndex)> stack,
        int level,
        int headingStart,
        int headingEnd,
        List<string> paths
    )
    {
        // Collect ancestor headings; stack iterates top-to-bottom, so reverse for root-first order.
        var parts = new List<(int Start, int End)>();
        foreach (var h in stack)
            parts.Add((h.HeadingStart, h.HeadingEnd));
        parts.Reverse();
        parts.Add((headingStart, headingEnd));

        const string Sep = " > ";
        int totalLen = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                totalLen += Sep.Length;
            totalLen += parts[i].End - parts[i].Start;
        }

        var path = string.Create(
            totalLen,
            (src, parts, Sep),
            static (span, state) =>
            {
                int pos = 0;
                for (int i = 0; i < state.parts.Count; i++)
                {
                    if (i > 0)
                    {
                        state.Sep.AsSpan().CopyTo(span[pos..]);
                        pos += state.Sep.Length;
                    }

                    var (s, e) = state.parts[i];
                    int partLen = e - s;
                    state.src.AsSpan(s, partLen).CopyTo(span[pos..]);
                    pos += partLen;
                }
            }
        );

        paths.Add(path);
        return paths.Count - 1;
    }

    // ── Find first H1 ──────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="src"/> for the first H1 heading and sets
    /// <paramref name="textStart"/> / <paramref name="textEnd"/> to its text bounds.
    /// Both remain zero when no H1 is found.
    /// </summary>
    private static void FindFirstH1(string src, ref int textStart, ref int textEnd)
    {
        int pos = 0,
            len = src.Length;

        while (pos < len)
        {
            int lineStart = pos,
                lineEnd = SkipToNextLine(src, pos);
            pos = AdvancePastLineEnding(src, lineEnd);

            if (
                TryHeading(
                    src.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r'),
                    lineStart,
                    out int foundLevel,
                    out textStart,
                    out textEnd
                )
                && foundLevel == 1
            )
                return;
        }
    }

    // ── ArrayPool growth ───────────────────────────────────────

    /// <summary>
    /// Doubles a rented array by allocating a larger one, copying existing items, and
    /// returning the original array to the pool.
    /// </summary>
    private static T[] Grow<T>(ArrayPool<T> pool, T[] arr, int count)
    {
        var next = pool.Rent(arr.Length * 2);
        Array.Copy(arr, next, count);
        pool.Return(arr);
        return next;
    }

    // ── Low-level helpers ──────────────────────────────────────

    /// <summary>Returns the index of the character just before the line ending at or after <paramref name="pos"/>.</summary>
    private static int SkipToNextLine(string s, int pos)
    {
        int len = s.Length;
        if (pos >= len)
            return pos;

        int end = pos;
        while (end < len && s[end] != '\n' && s[end] != '\r')
            end++;

        return end;
    }

    /// <summary>Advances past the line ending at <paramref name="end"/>, handling both <c>\n</c> and <c>\r\n</c>.</summary>
    private static int AdvancePastLineEnding(string s, int end)
    {
        int len = s.Length;
        if (end >= len)
            return end;

        return s[end] == '\r' && end + 1 < len && s[end + 1] == '\n' ? end + 2 : end + 1;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    /// <summary>
    /// Materialises a list of source line ranges into a single joined <see cref="ReadOnlyMemory{T}"/>,
    /// with each line separated by a newline character.
    /// </summary>
    private static ReadOnlyMemory<char> JoinRanges(string src, List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0)
            return default;

        int totalLen = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (i > 0)
                totalLen++;
            totalLen += ranges[i].End - ranges[i].Start;
        }

        var str = string.Create(
            totalLen,
            (src, ranges),
            static (span, state) =>
            {
                int pos = 0;
                for (int i = 0; i < state.ranges.Count; i++)
                {
                    if (i > 0)
                        span[pos++] = '\n';

                    var (s, e) = state.ranges[i];
                    int rangeLen = e - s;
                    state.src.AsSpan(s, rangeLen).CopyTo(span[pos..]);
                    pos += rangeLen;
                }
            }
        );

        return str.AsMemory();
    }

    // ── Line matchers ──────────────────────────────────────────

    /// <summary>
    /// Tries to parse an ATX heading from <paramref name="line"/>.
    /// On success sets <paramref name="level"/> (1–6) and the absolute source bounds
    /// <paramref name="textStart"/> / <paramref name="textEnd"/> of the heading text (no <c>#</c> markers or trailing spaces).
    /// </summary>
    private static bool TryHeading(
        ReadOnlySpan<char> line,
        int lineAbs,
        out int level,
        out int textStart,
        out int textEnd
    )
    {
        level = 0;
        textStart = 0;
        textEnd = 0;

        if (line.IsEmpty || line[0] != '#')
            return false;

        int i = 0;
        while (i < line.Length && i < 7 && line[i] == '#')
            i++;

        level = i;
        if (level > 6 || i >= line.Length || line[i] != ' ')
            return false;

        while (i < line.Length && line[i] == ' ')
            i++;

        if (i >= line.Length)
            return false;

        int s = i,
            e = line.Length;

        // Strip optional trailing "## markers ##" from ATX headings.
        while (e > s && line[e - 1] == ' ')
            e--;
        while (e > s && line[e - 1] == '#')
            e--;
        while (e > s && line[e - 1] == ' ')
            e--;

        textStart = lineAbs + s;
        textEnd = lineAbs + e;

        return true;
    }

    /// <summary>
    /// Tries to match a YAML-style fence line of at least three <paramref name="c"/> characters
    /// at the start of the source, advancing <paramref name="pos"/> past the fence and its newline.
    /// </summary>
    private static bool MatchFence(string src, ref int pos, char c)
    {
        int len = src.Length;
        if (pos >= len || src[pos] != c)
            return false;

        int i = pos;
        while (i < len && src[i] == c)
            i++;

        if (i - pos < 3)
            return false;

        while (i < len && src[i] != '\n')
        {
            if (src[i] != ' ' && src[i] != '\t')
                return false;
            i++;
        }

        pos = i < len ? i + 1 : i;
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="line"/> is a front-matter fence:
    /// three or more <paramref name="c"/> characters followed only by optional spaces.
    /// </summary>
    private static bool IsFenceLine(ReadOnlySpan<char> line, char c)
    {
        if (line.Length < 3 || line[0] != c)
            return false;

        int i = 0;
        while (i < line.Length && line[i] == c)
            i++;

        if (i < 3)
            return false;

        while (i < line.Length)
        {
            if (line[i] != ' ' && line[i] != '\t')
                return false;
            i++;
        }

        return true;
    }

    /// <summary>
    /// Matches a bare XML open tag on its own line, e.g. <c>&lt;example&gt;</c>.
    /// Sets <paramref name="tagStart"/> / <paramref name="tagEnd"/> to the absolute source bounds
    /// of the tag name (excluding the angle brackets).
    /// </summary>
    private static bool TryXmlOpen(
        ReadOnlySpan<char> line,
        int lineAbs,
        out int tagStart,
        out int tagEnd
    )
    {
        tagStart = 0;
        tagEnd = 0;

        if (line.Length < 3 || line[0] != '<')
            return false;

        int i = 1;
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '-'))
            i++;

        if (i == 1 || i >= line.Length || line[i] != '>')
            return false;

        // Reject tags with trailing content on the same line.
        if (i + 1 < line.Length)
            return false;

        tagStart = lineAbs + 1;
        tagEnd = lineAbs + i;

        return true;
    }

    /// <summary>
    /// Matches a bare XML close tag on its own line, e.g. <c>&lt;/example&gt;</c>, and verifies
    /// its name matches the previously captured open tag bounds in <paramref name="src"/>.
    /// </summary>
    private static bool TryXmlClose(
        string src,
        ReadOnlySpan<char> line,
        int openTagStart,
        int openTagEnd
    )
    {
        if (line.Length < 4 || line[0] != '<' || line[1] != '/')
            return false;

        int i = 2;
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '-'))
            i++;

        if (i == 2 || i >= line.Length || line[i] != '>')
            return false;

        if (i + 1 < line.Length)
            return false;

        return src.AsSpan(openTagStart, openTagEnd - openTagStart)
            .SequenceEqual(line.Slice(2, i - 2));
    }

    /// <summary>
    /// Matches a code fence opening line (<c>```lang</c>).
    /// Sets <paramref name="langStart"/> / <paramref name="langEnd"/> to the absolute source
    /// bounds of the language identifier when present; both are zero when absent.
    /// </summary>
    private static bool TryFenceOpen(
        ReadOnlySpan<char> line,
        int lineAbs,
        out int langStart,
        out int langEnd
    )
    {
        langStart = 0;
        langEnd = 0;

        if (line.Length < 3 || line[0] != '`')
            return false;

        int i = 0;
        while (i < line.Length && line[i] == '`')
            i++;

        if (i < 3)
            return false;

        while (i < line.Length && IsSpace(line[i]))
            i++;

        if (i < line.Length)
        {
            langStart = lineAbs + i;
            langEnd = lineAbs + line.Length;
        }

        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="line"/> is a code fence closing line —
    /// three or more backticks followed only by optional whitespace.
    /// </summary>
    private static bool IsFenceClose(ReadOnlySpan<char> line)
    {
        if (line.Length < 3 || line[0] != '`')
            return false;

        int i = 0;
        while (i < line.Length && line[i] == '`')
            i++;

        if (i < 3)
            return false;

        while (i < line.Length)
        {
            if (!IsSpace(line[i]))
                return false;
            i++;
        }

        return true;
    }
}
