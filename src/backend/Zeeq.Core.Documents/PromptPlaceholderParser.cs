using System.Buffers;
using System.Text.RegularExpressions;

namespace Zeeq.Core.Documents;

/// <summary>
/// Extracts and substitutes <c>zeeq_placeholder</c> regions in organization prompt documents.
/// </summary>
/// <remarks>
/// A prompt document may declare repository-customizable regions:
///
/// <code>
/// &lt;zeeq_placeholder name="testing_rules" displayName="Testing rules" description="How to test"&gt;
/// Use the project's default test runner.
/// &lt;/zeeq_placeholder&gt;
/// </code>
///
/// Flow: an MCP client sends <c>x-zeeq-prompts-repo: owner/repo</c> with <c>prompts/get</c>.
/// <c>DynamicPromptsService</c> resolves that repository, loads the placeholder values it saved for
/// the requested prompt, and calls <see cref="Substitute" />. Placeholders without an override fall
/// back to the tag body, so a prompt always renders sensibly with no repository context at all.
///
/// The tag body is deliberately <b>not</b> parsed as XML. Prompt bodies are markdown and routinely
/// contain <c>&lt;</c>, <c>&amp;</c>, and code fences that a conformant XML reader would reject; only
/// the opening tag's attributes are tokenized.
///
/// Two entry points share one scanner because they have opposite cost profiles:
/// <list type="bullet">
/// <item><description>
/// <see cref="Parse" /> is the cold editing path (a user expanding a prompt in the repository
/// configuration UI). It materializes every attribute.
/// </description></item>
/// <item><description>
/// <see cref="Substitute" /> is the hot retrieval path, called on every <c>prompts/get</c>. It reads
/// only <c>name</c>, keeps default values as ranges into the source, and allocates exactly one
/// string — the result.
/// </description></item>
/// </list>
/// </remarks>
public static partial class PromptPlaceholderParser
{
    private const string OpenTagPrefix = "<zeeq_placeholder";
    private const string CloseTag = "</zeeq_placeholder>";

    /// <summary>
    /// Matches one complete, well-formed placeholder region.
    /// </summary>
    /// <remarks>
    /// The attribute region <c>(?:"[^"]*"|[^"&gt;])*</c> alternates between a quoted value (which may
    /// contain <c>&gt;</c>) and any other non-quote character. The body uses a tempered scan so a
    /// malformed unclosed region cannot consume a later valid placeholder's closing tag.
    /// <see cref="RegexOptions.Singleline" /> lets the body span newlines; <c>RegexOptions.Compiled</c>
    /// is intentionally absent because it is redundant for a generated regex.
    /// </remarks>
    private const string RegionPattern =
        OpenTagPrefix
        + "\\b(?:\"[^\"]*\"|[^\">])*>"
        + "(?:(?!<zeeq_placeholder\\b|</zeeq_placeholder>).)*"
        + CloseTag;

    /// <summary>
    /// Reusable splice callback, held in a field so no delegate is allocated per call.
    /// </summary>
    private static readonly SpanAction<
        char,
        (string Content, PlaceholderSlice[] Slices, int Count)
    > SpliceAction = static (destination, state) =>
    {
        var source = state.Content.AsSpan();
        var written = 0;
        var cursor = 0;

        for (var index = 0; index < state.Count; index++)
        {
            var slice = state.Slices[index];

            // Literal run between the previous region and this one.
            source[cursor..slice.RegionStart].CopyTo(destination[written..]);
            written += slice.RegionStart - cursor;

            // Replacement: either the configured override, or the tag body sliced in place.
            var value = slice.Override is not null
                ? slice.Override.AsSpan()
                : source.Slice(slice.DefaultStart, slice.DefaultLength);
            value.CopyTo(destination[written..]);
            written += value.Length;

            cursor = slice.RegionStart + slice.RegionLength;
        }

        source[cursor..].CopyTo(destination[written..]);
    };

    /// <summary>
    /// Indicates whether a document declares any placeholder region.
    /// </summary>
    /// <remarks>
    /// Callers use this to skip work that only matters for templated prompts. In
    /// <c>DynamicPromptsService</c> it gates the repository lookup and prompt-configuration read, so a
    /// document with no placeholders costs one vectorized ordinal scan and no database round trip at
    /// all. Most organization prompts are expected to be plain documents, making this the highest
    /// leverage check on the retrieval path.
    /// </remarks>
    /// <param name="content">Raw prompt document body; may be null or empty.</param>
    /// <returns><see langword="true" /> when at least one opening tag marker is present.</returns>
    public static bool ContainsPlaceholders(string? content) =>
        !string.IsNullOrEmpty(content) && content.Contains(OpenTagPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Extracts every declared placeholder for the repository configuration editing surface.
    /// </summary>
    /// <remarks>
    /// Cold path — allocates a record and attribute strings per placeholder, which is the point: the
    /// UI needs the display metadata. Malformed regions (an unclosed tag, a missing quote) simply do
    /// not match and are skipped, which is what lets the API surface report "declared placeholders"
    /// without the caller pre-validating the document.
    ///
    /// Duplicate names are returned as-is rather than deduplicated, so the editing surface can show
    /// the author that a name was reused; override lookup treats them as one key.
    /// </remarks>
    /// <param name="content">Raw prompt document body.</param>
    /// <returns>Declared placeholders in document order; empty when none are present.</returns>
    public static IReadOnlyList<PromptPlaceholder> Parse(string? content)
    {
        if (!ContainsPlaceholders(content))
        {
            return [];
        }

        var source = content!.AsSpan();
        var placeholders = new List<PromptPlaceholder>();

        foreach (var match in PlaceholderRegionExpression().EnumerateMatches(source))
        {
            var region = source.Slice(match.Index, match.Length);
            var openTagEnd = FindOpenTagEnd(region);
            if (openTagEnd < 0)
            {
                continue;
            }

            var attributes = region[OpenTagPrefix.Length..openTagEnd];
            if (!TryGetAttribute(attributes, "name", out var name) || name.IsEmpty)
            {
                // A region without a usable name can never be overridden, so it is not an editable
                // placeholder. It still renders its default at retrieval time.
                continue;
            }

            var body = ReadBody(region, openTagEnd);

            placeholders.Add(
                new PromptPlaceholder(
                    Name: name.ToString(),
                    DisplayName: TryGetAttribute(attributes, "displayName", out var displayName)
                    && !displayName.IsEmpty
                        ? displayName.ToString()
                        : null,
                    Description: TryGetAttribute(attributes, "description", out var description)
                    && !description.IsEmpty
                        ? description.ToString()
                        : null,
                    DefaultValue: body.ToString()
                )
            );
        }

        return placeholders;
    }

    /// <summary>
    /// Renders a prompt body, replacing each placeholder region with its override or default.
    /// </summary>
    /// <remarks>
    /// Hot path. Runs unconditionally on every retrieval so raw placeholder markup can never reach an
    /// MCP client, including when there is no header, no repository match, and no saved configuration.
    /// The guarantee is precise: no <b>well-formed</b> region survives. Malformed markup does not
    /// match and passes through untouched — surfacing that to the author belongs in the parse-preview
    /// API, not in a runtime guess at intent.
    ///
    /// Allocation shape: the no-placeholder document returns the original instance; a substituting
    /// call allocates exactly one string. Default values are copied straight out of
    /// <paramref name="content" /> as spans and are never materialized, and placeholder names probe
    /// <paramref name="overrides" /> through a span alternate lookup so no name string is created
    /// either.
    /// </remarks>
    /// <param name="content">Raw prompt document body.</param>
    /// <param name="overrides">
    /// Repository-specific values keyed by placeholder name, or <see langword="null" /> to render
    /// defaults only. Should be built with <see cref="StringComparer.Ordinal" />; a dictionary with an
    /// incompatible comparer (a deserialized one, for example) is normalized once rather than
    /// silently ignored.
    /// </param>
    /// <returns>
    /// The rendered body. Returns <paramref name="content" /> itself when there is nothing to replace.
    /// </returns>
    public static string Substitute(string content, Dictionary<string, string>? overrides)
    {
        if (!ContainsPlaceholders(content))
        {
            return content;
        }

        var lookup = BuildLookup(overrides, out var hasOverrides);

        var source = content.AsSpan();
        var slices = ArrayPool<PlaceholderSlice>.Shared.Rent(8);
        var count = 0;
        var finalLength = content.Length;

        try
        {
            foreach (var match in PlaceholderRegionExpression().EnumerateMatches(source))
            {
                var region = source.Slice(match.Index, match.Length);
                var openTagEnd = FindOpenTagEnd(region);
                if (openTagEnd < 0)
                {
                    continue;
                }

                if (
                    !TryGetAttribute(region[OpenTagPrefix.Length..openTagEnd], "name", out var name)
                    || name.IsEmpty
                )
                {
                    // NOTE: Unnamed or malformed regions are not valid placeholders. Leave them
                    // untouched so authoring mistakes remain visible instead of silently deleting
                    // markup around the body.
                    continue;
                }

                string? overrideValue = null;
                if (hasOverrides && lookup.TryGetValue(name, out var configured))
                {
                    overrideValue = configured;
                }

                // Trimming is applied to the indices rather than through Span.Trim so the offset
                // back into the source string survives for the copy phase below.
                var (bodyStart, bodyLength) = MeasureBody(region, openTagEnd);

                if (count == slices.Length)
                {
                    Grow(ref slices, count);
                }

                finalLength += (overrideValue?.Length ?? bodyLength) - match.Length;
                slices[count++] = new PlaceholderSlice(
                    RegionStart: match.Index,
                    RegionLength: match.Length,
                    DefaultStart: match.Index + bodyStart,
                    DefaultLength: bodyLength,
                    Override: overrideValue
                );
            }

            // The marker was present but nothing well-formed matched: return the original instance
            // instead of allocating an identical copy.
            if (count == 0)
            {
                return content;
            }

            return string.Create(finalLength, (content, slices, count), SpliceAction);
        }
        finally
        {
            // clearArray because the slice holds a string reference the pool must not keep alive.
            ArrayPool<PlaceholderSlice>.Shared.Return(slices, clearArray: true);
        }
    }

    /// <summary>
    /// Builds the span-capable lookup used to resolve placeholder names without allocating.
    /// </summary>
    /// <remarks>
    /// <c>GetAlternateLookup</c> requires a comparer implementing <c>IAlternateEqualityComparer</c>.
    /// A dictionary that arrived from a deserializer carries whatever comparer that deserializer
    /// chose, so it is rebuilt once here. Silently treating an unsupported comparer as "no overrides"
    /// would drop every repository customization with no visible failure, which is the worst
    /// available outcome.
    /// </remarks>
    private static Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> BuildLookup(
        Dictionary<string, string>? overrides,
        out bool hasOverrides
    )
    {
        hasOverrides = overrides is { Count: > 0 };
        if (!hasOverrides)
        {
            return default;
        }

        return overrides!.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup)
            ? lookup
            : new Dictionary<string, string>(overrides, StringComparer.Ordinal).GetAlternateLookup<
                ReadOnlySpan<char>
            >();
    }

    /// <summary>
    /// Locates the <c>&gt;</c> that closes the opening tag, ignoring any inside quoted values.
    /// </summary>
    /// <remarks>
    /// A plain <c>IndexOf('&gt;')</c> would split the tag in the wrong place for
    /// <c>description="use the &gt; operator"</c>. The region pattern deliberately admits that case,
    /// so the splitter has to honor it too.
    /// </remarks>
    /// <param name="region">The full matched region, starting at <c>&lt;zeeq_placeholder</c>.</param>
    /// <returns>Index of the closing angle bracket, or <c>-1</c> when the tag is malformed.</returns>
    private static int FindOpenTagEnd(ReadOnlySpan<char> region)
    {
        var inQuotes = false;

        for (var index = OpenTagPrefix.Length; index < region.Length; index++)
        {
            var current = region[index];

            if (current == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (current == '>' && !inQuotes)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Computes the trimmed body range within a matched region.
    /// </summary>
    /// <remarks>
    /// Authors write the default value on its own lines, so the surrounding newlines are formatting
    /// rather than content. Returning offsets instead of a trimmed span keeps the value addressable
    /// in the original string for the zero-copy splice.
    /// </remarks>
    /// <param name="region">The full matched region.</param>
    /// <param name="openTagEnd">Index of the opening tag's closing angle bracket.</param>
    /// <returns>Region-relative start offset and length of the trimmed body.</returns>
    private static (int Start, int Length) MeasureBody(ReadOnlySpan<char> region, int openTagEnd)
    {
        var start = openTagEnd + 1;
        var end = region.Length - CloseTag.Length;

        while (start < end && char.IsWhiteSpace(region[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(region[end - 1]))
        {
            end--;
        }

        return (start, end - start);
    }

    /// <summary>
    /// Returns the trimmed body of a matched region as a span.
    /// </summary>
    private static ReadOnlySpan<char> ReadBody(ReadOnlySpan<char> region, int openTagEnd)
    {
        var (start, length) = MeasureBody(region, openTagEnd);

        return region.Slice(start, length);
    }

    /// <summary>
    /// Reads one attribute value out of an opening tag's attribute region.
    /// </summary>
    /// <remarks>
    /// This tokenizes whole <c>name="value"</c> pairs rather than searching for a substring, which is
    /// a correctness requirement and not just a performance one: <c>displayName="…"</c> contains the
    /// text <c>name=</c>, so an index scan would silently read the wrong attribute. Comparing complete
    /// tokens also makes attribute order and omission free.
    ///
    /// Parsing stops at the first structural surprise (no <c>=</c>, an unquoted value, an unterminated
    /// quote) and reports failure rather than guessing.
    /// </remarks>
    /// <param name="attributes">Text between <c>&lt;zeeq_placeholder</c> and its closing bracket.</param>
    /// <param name="attributeName">Attribute to find, compared ordinally.</param>
    /// <param name="value">The attribute value, excluding quotes, when found.</param>
    /// <returns><see langword="true" /> when the attribute is present and well-formed.</returns>
    private static bool TryGetAttribute(
        ReadOnlySpan<char> attributes,
        ReadOnlySpan<char> attributeName,
        out ReadOnlySpan<char> value
    )
    {
        while (!attributes.IsEmpty)
        {
            attributes = attributes.TrimStart();

            var equals = attributes.IndexOf('=');
            if (equals < 0)
            {
                break;
            }

            var candidate = attributes[..equals].TrimEnd();
            var remainder = attributes[(equals + 1)..].TrimStart();
            if (remainder.IsEmpty || remainder[0] != '"')
            {
                break;
            }

            remainder = remainder[1..];

            var closingQuote = remainder.IndexOf('"');
            if (closingQuote < 0)
            {
                break;
            }

            if (candidate.SequenceEqual(attributeName))
            {
                value = remainder[..closingQuote];

                return true;
            }

            attributes = remainder[(closingQuote + 1)..];
        }

        value = default;

        return false;
    }

    /// <summary>
    /// Replaces the pooled slice buffer with a larger one, preserving recorded entries.
    /// </summary>
    private static void Grow(ref PlaceholderSlice[] buffer, int count)
    {
        var larger = ArrayPool<PlaceholderSlice>.Shared.Rent(buffer.Length * 2);
        buffer.AsSpan(0, count).CopyTo(larger);
        ArrayPool<PlaceholderSlice>.Shared.Return(buffer, clearArray: true);
        buffer = larger;
    }

    [GeneratedRegex(RegionPattern, RegexOptions.Singleline)]
    private static partial Regex PlaceholderRegionExpression();

    /// <summary>
    /// One recorded replacement: the region to remove and the value that takes its place.
    /// </summary>
    /// <remarks>
    /// The default value is kept as an offset pair into the source document rather than a string, so
    /// a prompt with many placeholders and no overrides allocates nothing while scanning.
    /// </remarks>
    private readonly record struct PlaceholderSlice(
        int RegionStart,
        int RegionLength,
        int DefaultStart,
        int DefaultLength,
        string? Override
    );
}
