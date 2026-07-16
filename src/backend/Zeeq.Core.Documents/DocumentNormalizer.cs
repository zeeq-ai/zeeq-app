using System.Text;

namespace Zeeq.Core.Documents;

/// <summary>
/// Shared normalizer for document paths, normalized titles, and keywords.
/// </summary>
/// <remarks>
/// This belongs to the library document write path, not the markdown parser. Headings are
/// deliberately not normalized because they remain as-authored for display and search.
/// </remarks>
public static class DocumentNormalizer
{
    /// <summary>
    /// Normalizes a value to lower-case, stripping characters outside <c>[a-z0-9/_\-+. ]</c>.
    /// </summary>
    /// <param name="value">The title, keyword, or other search-facing value to normalize.</param>
    /// <returns>The normalized value, trimmed after disallowed characters are removed.</returns>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value.ToLowerInvariant())
        {
            if (IsAllowedValueCharacter(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Normalizes a path to lower-case, allowed path characters, a leading slash, and a <c>.md</c> suffix.
    /// </summary>
    /// <param name="path">The caller-supplied document path.</param>
    /// <returns>The normalized absolute markdown path used as document identity within a library.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="path"/> is empty or whitespace, or contains a relative
    /// <c>.</c> or <c>..</c> segment.
    /// </exception>
    public static string NormalizePath(string path)
    {
        // If path starts with "zeeq:" or "zeeq://", strip the prefix and treat the rest as a path
        if (path.StartsWith("zeeq:", StringComparison.OrdinalIgnoreCase))
        {
            path = path["zeeq:".Length..];
        }
        else if (path.StartsWith("zeeq://", StringComparison.OrdinalIgnoreCase))
        {
            path = path["zeeq://".Length..];
        }

        var trimmedPath = path.Trim().Trim('/').Trim('@').Replace('\\', '/');

        if (trimmedPath.Length == 0)
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        foreach (var segment in trimmedPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException(
                    "Relative path segments are not allowed.",
                    nameof(path)
                );
            }
        }

        var builder = new StringBuilder(path.Length + 4);

        var previousWasSlash = false;

        foreach (var character in trimmedPath.ToLowerInvariant())
        {
            if (character == '/')
            {
                if (!previousWasSlash)
                {
                    builder.Append(character);
                    previousWasSlash = true;
                }

                continue;
            }

            previousWasSlash = false;
            if (IsAllowedPathCharacter(character))
            {
                builder.Append(character);
            }
        }

        var normalized = builder.ToString().Trim('/');
        normalized = "/" + normalized;

        return normalized.EndsWith(".md", StringComparison.Ordinal)
            ? normalized
            : normalized + ".md";
    }

    /// <summary>
    /// Normalizes, trims, and deduplicates keywords while preserving first-seen order.
    /// </summary>
    /// <param name="keywords">The parser-derived keyword list.</param>
    /// <returns>Normalized keywords with empty values removed and first-seen order preserved.</returns>
    public static string[] NormalizeKeywords(IReadOnlyList<string> keywords)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(keywords.Count);

        foreach (var keyword in keywords)
        {
            var value = Normalize(keyword.Trim());
            if (value.Length > 0 && seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return [.. normalized];
    }

    private static bool IsAllowedValueCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+' or '.' or ' ';

    private static bool IsAllowedPathCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+' or '.';
}
