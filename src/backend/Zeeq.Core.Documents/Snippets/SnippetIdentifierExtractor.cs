using System.Text.RegularExpressions;

namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// Extracts code identifiers (camelCase, PascalCase, snake_case, dotted paths) from source text
/// for the exact-identifier boost in hybrid snippet search.
/// </summary>
/// <remarks>
/// Used on both sides of search: at index time over a code snippet's content to populate
/// <see cref="LibraryDocumentSnippet.Identifiers"/>, and at query time over the query text so the
/// two overlap sets can be matched with a Postgres array-overlap (<c>&amp;&amp;</c>) boost. Output is
/// lowercased (case-insensitive matching), deduplicated, filtered to identifiers of at least the
/// caller-supplied minimum length (<see cref="IndexMinLength"/> or <see cref="QueryMinLength"/>),
/// and capped at <see cref="MaxIdentifiers"/> per input to keep the array column bounded.
/// </remarks>
public static partial class SnippetIdentifierExtractor
{
    /// <summary>
    /// Minimum identifier length for index-time extraction over code-snippet content (shorter
    /// tokens are too noisy to boost on).
    /// </summary>
    /// <remarks>
    /// Raised to 14 for a stricter precision-over-recall boost signal: short and medium-length
    /// tokens like <c>id</c>/<c>db</c>/<c>ctx</c>/<c>handler</c>/<c>request</c> are common enough
    /// across unrelated snippets to dilute the array-overlap match, so only longer, more distinctive
    /// names (types, qualified members, multi-word snake_case identifiers) are kept. Code content is
    /// abundant per document, so this budget can afford to be strict.
    /// </remarks>
    public const int IndexMinLength = 14;

    /// <summary>
    /// Minimum identifier length for query-time extraction over free-text search queries.
    /// </summary>
    /// <remarks>
    /// Deliberately lower than <see cref="IndexMinLength"/>: a search query is a handful of
    /// user-typed words, not a code snippet, so applying the same 14-char floor would empty out
    /// <c>queryIdentifiers</c> for most realistic queries (<c>handler</c>, <c>logger</c>,
    /// <c>service</c> are all under 14 chars) and silently drop the identifier-overlap boost
    /// signal for the common case. 6 still filters the truly noisy short tokens (<c>id</c>,
    /// <c>db</c>, <c>ctx</c>) while keeping mid-length identifiers a user is likely to type.
    /// </remarks>
    public const int QueryMinLength = 6;

    /// <summary>Maximum identifiers returned per input, to bound the stored array size.</summary>
    private const int MaxIdentifiers = 64;

    /// <summary>
    /// Matches dotted paths (<c>Foo.Bar.Baz</c>) and single identifiers. The identifier body
    /// allows letters, digits, and underscores, so camelCase, PascalCase, and snake_case are all
    /// captured; a leading letter or underscore keeps pure-number tokens out.
    /// </summary>
    [GeneratedRegex(
        @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex IdentifierPattern();

    /// <summary>Double- and single-quoted string literals, including escaped quotes.</summary>
    [GeneratedRegex(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteralPattern();

    /// <summary>C-style block comments (also matches doc comments and CSS/SQL block comments).</summary>
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    /// <summary><c>//</c> line comments, universal across the C-family languages this indexes.</summary>
    [GeneratedRegex(@"//[^\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex SlashLineCommentPattern();

    /// <summary>
    /// <c>#</c> line comments (Python, Ruby, shell, YAML, Elixir, PHP, ...). Applied
    /// unconditionally: none of the languages this indexes for use a bare leading <c>#</c> for
    /// anything else identifier extraction would want to keep.
    /// </summary>
    [GeneratedRegex(@"#[^\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex HashLineCommentPattern();

    /// <summary>
    /// SQL/Lua/Haskell-style <c>--</c> line comments. Requires a space/tab after the dashes so a
    /// C-family decrement operator (<c>count--;</c>, no space) is never mistaken for a comment —
    /// a real SQL comment is almost always written <c>-- like this</c>.
    /// </summary>
    [GeneratedRegex(@"--[ \t].*", RegexOptions.CultureInvariant)]
    private static partial Regex DashLineCommentPattern();

    /// <summary>
    /// Reserved words and built-in primitive type names across the languages this snippet corpus
    /// spans: C#, Java, Python, Ruby, JavaScript/TypeScript, Rust, Go, Elixir, C, C++, SQL, PHP.
    /// These are syntax, not named entities — excluding them is what keeps the identifier boost
    /// signal on class/interface/type/member/function names instead of drowning in keywords that
    /// appear in nearly every snippet regardless of what the snippet is actually about.
    /// </summary>
    /// <remarks>
    /// Only entries at least <see cref="IndexMinLength"/> characters long are listed — anything
    /// shorter (<c>public</c>, <c>class</c>, <c>void</c>, <c>string</c>, ...) is already dropped by
    /// the length filter in <see cref="AddCandidate"/> at that cutoff, so listing it here would be
    /// dead weight. At the lower <see cref="QueryMinLength"/> cutoff used for query text, shorter
    /// keywords are not screened by this list — acceptable there since a search query is user-typed
    /// words, not code, and rarely consists of bare language keywords.
    /// </remarks>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        // C++ operator-keywords.
        "reinterpret_cast",
        // Elixir special forms.
        "unquote_splicing",
        // SQL keywords/modifiers.
        "auto_increment",
        "current_timestamp",
    };

    /// <summary>
    /// Strips string-literal contents and comments so extraction runs over code, not prose. Log
    /// message text, docstrings, and inline comments are natural-language and produce noisy,
    /// low-quality identifier candidates (see remarks on <see cref="Extract"/>).
    /// </summary>
    private static string StripNoise(string content)
    {
        var withoutStrings = StringLiteralPattern().Replace(content, " ");
        var withoutBlockComments = BlockCommentPattern().Replace(withoutStrings, " ");
        var withoutSlashComments = SlashLineCommentPattern().Replace(withoutBlockComments, " ");
        var withoutHashComments = HashLineCommentPattern().Replace(withoutSlashComments, " ");
        return DashLineCommentPattern().Replace(withoutHashComments, " ");
    }

    /// <summary>
    /// Extracts and normalizes identifiers from <paramref name="content"/>.
    /// </summary>
    /// <param name="content">Code content or query text to scan.</param>
    /// <param name="minLength">
    /// Minimum identifier length to keep — pass <see cref="IndexMinLength"/> for code-snippet
    /// content or <see cref="QueryMinLength"/> for free-text search queries. Required (not
    /// defaulted) so every call site states its intent explicitly rather than silently inheriting
    /// whichever cutoff happens to suit the other caller.
    /// </param>
    /// <returns>
    /// Distinct lowercased identifiers (order preserved by first appearance), filtered to
    /// <paramref name="minLength"/>+ characters, reserved-word/built-in-type keywords removed, and
    /// capped at <see cref="MaxIdentifiers"/>. Empty when <paramref name="content"/> is null or
    /// blank.
    /// </returns>
    /// <remarks>
    /// Comments and string-literal contents are stripped before matching — they are natural-
    /// language prose (log messages, docstrings, <c>// TODO</c> notes), not code identifiers, and
    /// previously produced exactly the kind of noise (stray English words riding along with real
    /// identifiers) this extractor exists to avoid. At <see cref="IndexMinLength"/>, virtually every
    /// syntax keyword across the languages this indexes (<c>public</c>, <c>class</c>, <c>void</c>,
    /// <c>synchronized</c>, <c>interface</c>, ...) is already excluded by length alone; the small
    /// cross-language reserved-word list only covers the rare exceptions that happen to also be long
    /// (<c>reinterpret_cast</c>, <c>auto_increment</c>, ...), so what remains skews toward named
    /// entities: types, members, and call targets.
    /// </remarks>
    public static string[] Extract(string? content, int minLength)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var stripped = StripNoise(content);

        // Preserve first-seen order and dedupe case-insensitively; the set tracks lowercased forms.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (Match match in IdentifierPattern().Matches(stripped))
        {
            var token = match.Value;

            // The dotted path itself counts; also split into components so `Foo.Bar` matches a
            // query mentioning just `Bar`. Both are subject to the same length filter and cap.
            AddCandidate(token, minLength, seen, result);

            if (result.Count >= MaxIdentifiers)
            {
                break;
            }

            if (token.Contains('.', StringComparison.Ordinal))
            {
                foreach (var part in token.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    AddCandidate(part, minLength, seen, result);

                    if (result.Count >= MaxIdentifiers)
                    {
                        break;
                    }
                }
            }

            if (result.Count >= MaxIdentifiers)
            {
                break;
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Adds a single candidate token to the result if it passes the length filter, is not a
    /// reserved keyword or built-in type name, and is new.
    /// </summary>
    private static void AddCandidate(
        string token,
        int minLength,
        HashSet<string> seen,
        List<string> result
    )
    {
        if (token.Length < minLength || result.Count >= MaxIdentifiers)
        {
            return;
        }

        var lowered = token.ToLowerInvariant();

        if (ReservedWords.Contains(lowered))
        {
            return;
        }

        if (seen.Add(lowered))
        {
            result.Add(lowered);
        }
    }
}
