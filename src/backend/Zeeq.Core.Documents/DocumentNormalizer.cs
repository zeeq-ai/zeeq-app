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
    /// <summary>Database-backed maximum length for persisted skill prompt names.</summary>
    public const int MaxSkillNameLength = 512;

    /// <summary>Database-backed maximum length for persisted skill prompt descriptions.</summary>
    public const int MaxSkillDescriptionLength = 4096;

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

    /// <summary>
    /// Normalizes a skill prompt name into the persisted MCP prompt identifier.
    /// </summary>
    /// <remarks>
    /// This is intentionally separate from <see cref="Normalize" /> because MCP prompt names are
    /// identifiers, not search text. Future manual override APIs must pass user-entered skill names
    /// through this method before saving <see cref="LibraryDocument.ManualSkillName" />.
    /// </remarks>
    public static string NormalizePromptName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (character is '-' or '_')
            {
                if (!previousWasSeparator)
                {
                    builder.Append(character);
                    previousWasSeparator = true;
                }

                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-', '_');
    }

    /// <summary>
    /// Normalizes an optional skill prompt name and bounds it to the persisted column length.
    /// </summary>
    /// <remarks>
    /// Front-matter is external input. Bounding here keeps all write paths aligned with the
    /// database contract so oversized <c>name:</c> fields cannot fail ingestion at save time.
    /// </remarks>
    public static string? NormalizeOptionalPromptName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NormalizePromptName(value);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length <= MaxSkillNameLength)
        {
            return normalized;
        }

        var bounded = normalized[..MaxSkillNameLength].Trim('-', '_');

        return bounded.Length == 0 ? null : bounded;
    }

    /// <summary>
    /// Bounds an optional raw skill prompt description to the persisted column length.
    /// </summary>
    /// <remarks>
    /// Descriptions remain raw authored text for display. The only transformation here is a hard
    /// maximum length guard matching the Postgres model configuration.
    /// </remarks>
    public static string? BoundOptionalSkillDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= MaxSkillDescriptionLength
            ? value
            : value[..MaxSkillDescriptionLength];
    }

    private static bool IsAllowedValueCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+' or '.' or ' ';

    private static bool IsAllowedPathCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+' or '.';
}
