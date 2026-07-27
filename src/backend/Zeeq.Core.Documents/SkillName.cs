using System.Text.RegularExpressions;

namespace Zeeq.Core.Documents;

/// <summary>
/// Represents the MCP prompt name Zeeq exposes for a document marked as an organization skill.
/// </summary>
/// <remarks>
/// MCP prompt names are client-visible identifiers. Zeeq supports two shapes:
///
/// <list type="bullet">
/// <item>
/// <description>
/// Short names for globally unique skill names, for example
/// <c>dotnet-csharp-best-practices</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// Fully-qualified names for colliding or path-significant documents, for example
/// <c>zeeq-app:backend/dotnet-csharp-best-practices[review-guidance]</c>.
/// </description>
/// </item>
/// </list>
///
/// The fully-qualified shape is:
/// <c>{library}:{documentPathWithoutMd}[{skillName}]</c>.
///
/// The path keeps <c>/</c> separators because MCP clients tested during this change tolerate
/// slash-containing prompt names and render them in a useful command-like shape. The bracketed
/// skill-name suffix is explicit and legible; generated segments are normalized so literal
/// brackets do not appear inside the suffix.
///
/// Collision model:
/// <list type="bullet">
/// <item>
/// <description>Short names can collide across documents and should only be used when unique.</description>
/// </item>
/// <item>
/// <description>
/// Fully-qualified names are unique for a library/document-path/skill-name tuple.
/// </description>
/// </item>
/// <item>
/// <description>
/// Documents named <c>SKILL.md</c> omit the generic file segment and use the parent path as the
/// path identity. If the resolved skill name matches the parent segment, the bracketed suffix is
/// omitted; if it differs, the suffix records the authored skill identity.
/// </description>
/// </item>
/// </list>
/// </remarks>
public readonly partial record struct SkillName
{
    private const char LibrarySeparator = ':';
    private const char PathSeparator = '/';

    private SkillName(
        string value,
        bool isFullyQualified,
        string? library,
        IReadOnlyList<string> documentPathSegments,
        string skillName,
        string? shortDocumentId
    )
    {
        Value = value;
        IsFullyQualified = isFullyQualified;
        Library = library;
        DocumentPathSegments = documentPathSegments;
        NormalizedName = skillName;
        ShortDocumentId = shortDocumentId;
    }

    /// <summary>
    /// Gets the normalized prompt name sent to or received from the MCP client.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets whether this name contains library and document-path lookup parameters.
    /// </summary>
    public bool IsFullyQualified { get; }

    /// <summary>
    /// Gets the normalized library segment for fully-qualified names.
    /// </summary>
    public string? Library { get; }

    /// <summary>
    /// Gets the normalized document path segments for fully-qualified names.
    /// </summary>
    /// <remarks>
    /// The final document path segment is the file stem without the markdown extension. For
    /// <c>SKILL.md</c> documents, the generic <c>skill</c> file segment is omitted so the parent
    /// directory remains the meaningful path identity.
    /// </remarks>
    public IReadOnlyList<string> DocumentPathSegments { get; }

    /// <summary>
    /// Gets the normalized skill name segment.
    /// </summary>
    public string NormalizedName { get; }

    /// <summary>
    /// Gets the optional normalized document-id suffix used as a final collision tie-breaker.
    /// </summary>
    public string? ShortDocumentId { get; }

    /// <summary>
    /// Creates a short prompt name for a skill name that is unique across the prompt list.
    /// </summary>
    public static SkillName Short(string skillName)
    {
        var normalizedSkillName = NormalizeRequiredName(skillName, nameof(skillName));

        return new SkillName(
            normalizedSkillName,
            isFullyQualified: false,
            library: null,
            documentPathSegments: [],
            normalizedSkillName,
            shortDocumentId: null
        );
    }

    /// <summary>
    /// Creates a fully-qualified prompt name for a colliding or path-significant skill name.
    /// </summary>
    /// <remarks>
    /// Example:
    /// <c>FullyQualified("zeeq-app", "/backend/dotnet-csharp-best-practices.md", "Review Guidance")</c>
    /// produces <c>zeeq-app:backend/dotnet-csharp-best-practices[review-guidance]</c>.
    /// </remarks>
    public static SkillName FullyQualified(
        string library,
        string documentPath,
        string skillName,
        string? shortDocumentId = null
    )
    {
        var normalizedLibrary = NormalizeRequiredLibrary(library, nameof(library));
        var normalizedPathSegments = NormalizeDocumentPathSegments(documentPath);
        var normalizedSkillName = NormalizeRequiredName(skillName, nameof(skillName));
        var normalizedShortDocumentId = NormalizeOptionalName(shortDocumentId);

        return CreateFullyQualified(
            normalizedLibrary,
            normalizedPathSegments,
            normalizedSkillName,
            normalizedShortDocumentId
        );
    }

    /// <summary>
    /// Parses a prompt name produced by <see cref="Short" /> or <see cref="FullyQualified" />.
    /// </summary>
    /// <remarks>
    /// A value with a library separator is parsed as fully-qualified. Otherwise, it is parsed as a
    /// short skill name. Parsed fully-qualified values are normalized structurally rather than
    /// requiring byte-for-byte equality with the input.
    /// </remarks>
    public static bool TryParse(string? value, out SkillName promptName)
    {
        promptName = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var match = FullyQualifiedPattern().Match(trimmed);
        if (!match.Success)
        {
            return trimmed.Contains(LibrarySeparator, StringComparison.Ordinal)
                ? false
                : TryCreateShort(trimmed, out promptName);
        }

        var normalizedLibrary = NormalizeOptionalLibrary(match.Groups["library"].Value);
        var pathText = match.Groups["path"].Value;
        var skillNameText = match.Groups["name"].Success ? match.Groups["name"].Value : null;
        var shortDocumentIdText = match.Groups["id"].Success ? match.Groups["id"].Value : null;
        var normalizedPathSegments = NormalizeQualifiedPathSegments(pathText);
        var normalizedSkillName =
            NormalizeOptionalName(skillNameText) ?? normalizedPathSegments.LastOrDefault();
        var normalizedShortDocumentId = NormalizeOptionalName(shortDocumentIdText);
        if (
            normalizedLibrary is null
            || normalizedPathSegments.Count == 0
            || normalizedSkillName is null
            || (shortDocumentIdText is not null && normalizedShortDocumentId is null)
        )
        {
            return false;
        }

        promptName = CreateFullyQualified(
            normalizedLibrary,
            normalizedPathSegments,
            normalizedSkillName,
            normalizedShortDocumentId
        );

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Creates a short name from parsed input when no library separator is present.
    /// </summary>
    /// <remarks>
    /// Short names stay normalized identifiers. Characters useful in fully-qualified names, such
    /// as <c>:</c>, <c>/</c>, and brackets, are not preserved here because short names do not carry
    /// path identity.
    /// </remarks>
    private static bool TryCreateShort(string value, out SkillName promptName)
    {
        promptName = default;
        var normalized = NormalizeOptionalName(value);
        if (normalized is null)
        {
            return false;
        }

        promptName = Short(normalized);

        return true;
    }

    /// <summary>
    /// Builds a fully-qualified value from normalized structural parts.
    /// </summary>
    /// <remarks>
    /// The bracketed suffix is omitted when it would only repeat the final path segment. This is
    /// especially useful for <c>SKILL.md</c> documents where the parent directory is normally the
    /// skill package name.
    /// </remarks>
    private static SkillName CreateFullyQualified(
        string normalizedLibrary,
        IReadOnlyList<string> normalizedPathSegments,
        string normalizedSkillName,
        string? normalizedShortDocumentId
    )
    {
        var path = string.Join(PathSeparator, normalizedPathSegments);
        var value = $"{normalizedLibrary}{LibrarySeparator}{path}";

        if (
            !string.Equals(
                normalizedPathSegments[^1],
                normalizedSkillName,
                StringComparison.Ordinal
            )
        )
        {
            value += $"[{normalizedSkillName}]";
        }

        if (normalizedShortDocumentId is not null)
        {
            value += $"[{normalizedShortDocumentId}]";
        }

        return new SkillName(
            value,
            isFullyQualified: true,
            normalizedLibrary,
            normalizedPathSegments,
            normalizedSkillName,
            normalizedShortDocumentId
        );
    }

    /// <summary>
    /// Normalizes a required library segment and throws when it disappears.
    /// </summary>
    private static string NormalizeRequiredLibrary(string value, string parameterName)
    {
        var normalized = NormalizeOptionalLibrary(value);
        if (normalized is null)
        {
            throw new ArgumentException(
                "The prompt name library segment must contain at least one letter or digit after normalization.",
                parameterName
            );
        }

        return normalized;
    }

    /// <summary>
    /// Normalizes a required skill-name segment and throws when it disappears.
    /// </summary>
    private static string NormalizeRequiredName(string value, string parameterName)
    {
        var normalized = NormalizeOptionalName(value);
        if (normalized is null)
        {
            throw new ArgumentException(
                "The prompt name segment must contain at least one letter or digit after normalization.",
                parameterName
            );
        }

        return normalized;
    }

    /// <summary>
    /// Normalizes one optional library segment.
    /// </summary>
    /// <remarks>
    /// Library names use prompt-name normalization, so delimiters like <c>:</c> and <c>/</c> cannot
    /// leak into the generated structural prefix.
    /// </remarks>
    private static string? NormalizeOptionalLibrary(string? value) => NormalizeOptionalName(value);

    /// <summary>
    /// Normalizes one optional skill-name or path segment into the restricted prompt-name alphabet.
    /// </summary>
    /// <remarks>
    /// This is the delimiter safety valve: raw input containing brackets, colons, punctuation, or
    /// repeated separators is collapsed before a fully-qualified name is rendered.
    /// </remarks>
    private static string? NormalizeOptionalName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NormalizeSegment(value);

        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// Normalizes one atomic prompt-name segment without permitting structural delimiters.
    /// </summary>
    /// <remarks>
    /// This deliberately differs from <see cref="DocumentNormalizer.NormalizePromptName(string)"/>,
    /// which permits <c>/</c> for whole document paths. Here <c>/</c>, <c>:</c>, brackets, and
    /// other punctuation collapse to a single dash so properties like <see cref="Library"/>,
    /// <see cref="NormalizedName"/>, and <see cref="DocumentPathSegments"/> remain atomic.
    /// </remarks>
    private static string NormalizeSegment(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
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
    /// Converts a generated document path into normalized prompt-name path segments.
    /// </summary>
    /// <remarks>
    /// Document paths are normalized with the same rules as library documents, then the final
    /// segment drops the markdown extension. <c>SKILL.md</c> drops the generic final segment so the
    /// parent directory identifies the imported skill package.
    /// </remarks>
    private static IReadOnlyList<string> NormalizeDocumentPathSegments(string documentPath)
    {
        var normalizedPath = DocumentNormalizer.NormalizePath(documentPath);
        var rawSegments = normalizedPath.Split(
            PathSeparator,
            StringSplitOptions.RemoveEmptyEntries
        );
        if (rawSegments.Length == 0)
        {
            throw new ArgumentException(
                "The document path must contain at least one segment after normalization.",
                nameof(documentPath)
            );
        }

        var lastSegmentStem = Path.GetFileNameWithoutExtension(rawSegments[^1]);
        var segmentCount = string.Equals(lastSegmentStem, "skill", StringComparison.Ordinal)
            ? rawSegments.Length - 1
            : rawSegments.Length;
        if (segmentCount <= 0)
        {
            throw new ArgumentException(
                "The document path must contain a parent segment for SKILL.md documents.",
                nameof(documentPath)
            );
        }

        var segments = new List<string>(capacity: segmentCount);
        for (var index = 0; index < segmentCount; index++)
        {
            var rawSegment =
                index == rawSegments.Length - 1
                    ? Path.GetFileNameWithoutExtension(rawSegments[index])
                    : rawSegments[index];
            var normalizedSegment = NormalizeOptionalName(rawSegment);
            if (normalizedSegment is null)
            {
                throw new ArgumentException(
                    "The document path contains a segment that is empty after normalization.",
                    nameof(documentPath)
                );
            }

            segments.Add(normalizedSegment);
        }

        return segments;
    }

    /// <summary>
    /// Parses an already-rendered qualified path into normalized path segments.
    /// </summary>
    /// <remarks>
    /// This path is already extension-free. Unlike generated document paths, it must not run
    /// through markdown extension handling or <c>SKILL.md</c> parent folding again.
    /// </remarks>
    private static IReadOnlyList<string> NormalizeQualifiedPathSegments(string pathText)
    {
        var rawSegments = pathText.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<string>(capacity: rawSegments.Length);
        foreach (var rawSegment in rawSegments)
        {
            var normalizedSegment = NormalizeOptionalName(rawSegment);
            if (normalizedSegment is null)
            {
                return [];
            }

            segments.Add(normalizedSegment);
        }

        return segments;
    }

    /// <summary>
    /// Matches the generated fully-qualified prompt-name shape.
    /// </summary>
    /// <remarks>
    /// Regex owns the delimiter grammar only: <c>{library}:{path}[{skillName}][{id}]</c>. Semantic
    /// normalization and empty-segment rejection stay in the typed helper methods.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<library>[^:\[\]]+):(?<path>[^\[\]]+?)(?:\[(?<name>[^\[\]]+)\])?(?:\[(?<id>[^\[\]]+)\])?$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex FullyQualifiedPattern();
}
