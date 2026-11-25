using System.Text.RegularExpressions;

namespace Zeeq.Platform.CodeReviews;

/// <summary>A finding's immutable position and semantic label in an aggregate review.</summary>
public sealed record IndexedCodeReviewFinding(
    int GlobalOrdinal,
    string OccurrenceKey,
    string SemanticId,
    CodeReviewFacetOutput Facet,
    CodeReviewFindingOutput Finding
);

/// <summary>Assigns finding identities in raw document order. The V1 derivation is frozen.</summary>
public static class CodeReviewFindingIds
{
    /// <summary>Formats a review-specific short reference.</summary>
    public static string FormatShortReference(string reviewId, string occurrenceKey) =>
        $"{(reviewId.Length >= 5 ? reviewId[^5..] : reviewId).ToLowerInvariant()}.{occurrenceKey}";

    /// <summary>Assigns positions and unique semantic labels before filtering findings.</summary>
    public static IReadOnlyList<IndexedCodeReviewFinding> Assign(CodeReviewOutputDocument document)
    {
        var result = new List<IndexedCodeReviewFinding>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var facet in document.Reviews)
        {
            foreach (var finding in facet.Findings)
            {
                var ordinal = result.Count + 1;
                var candidate =
                    finding.Id is { } id && IsValid(id)
                        ? id
                        : Derive(finding.Summary, finding.File, facet.Facet, ordinal);
                var unique = candidate;
                var suffix = nextSuffix.GetValueOrDefault(candidate, 2);
                while (!used.Add(unique))
                {
                    var tail = $"-{suffix}";
                    unique =
                        candidate[..Math.Min(candidate.Length, 48 - tail.Length)].TrimEnd('-')
                        + tail;
                    suffix++;
                }
                nextSuffix[candidate] = suffix;
                result.Add(new(ordinal, $"F{ordinal}", unique, facet, finding));
            }
        }
        return result;
    }

    /// <summary>Normalizes an optional reviewer hint before it becomes a stamped XML ID.</summary>
    internal static string? NormalizeHint(string? hint, string file, string facet)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }
        if (hint == "missing-null-check")
        {
            return Derive(hint, file, facet, 1);
        }
        return IsValid(hint) ? hint : Derive(hint, file, facet, 1);
    }

    private static bool IsValid(string value) =>
        value.Length <= 48
        && Regex.IsMatch(
            value,
            "^[a-z][a-z0-9]*(-[a-z0-9]+){2,7}(-[0-9]+)?$",
            RegexOptions.CultureInvariant
        );

    private static string Derive(string source, string file, string facet, int ordinal)
    {
        static string[] Tokens(string value) =>
            Regex
                .Split(value.ToLowerInvariant(), "[^a-z0-9]+")
                .Where(token => token.Length > 0)
                .ToArray();
        var tokens = Tokens(source).ToList();
        if (tokens.Count > 0)
        {
            tokens[0] = tokens[0].TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        }
        while (tokens.Count > 0 && (tokens[0].Length == 0 || !char.IsAsciiLetter(tokens[0][0])))
        {
            tokens.RemoveAt(0);
        }
        if (tokens.Count < 3 || string.Join('-', tokens) == "missing-null-check")
        {
            tokens.AddRange(Tokens(Path.GetFileNameWithoutExtension(file.Replace('\\', '/'))));
        }
        if (tokens.Count < 3)
            tokens.AddRange(Tokens(facet));
        if (tokens.Count < 3)
            tokens.Add("finding");
        if (tokens.Count < 3)
            tokens.Add(ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var slug = string.Join('-', tokens.Take(8));
        slug = slug[..Math.Min(slug.Length, 48)].TrimEnd('-');
        if (!IsValid(slug))
        {
            var facetToken = Tokens(facet).FirstOrDefault(x => char.IsAsciiLetter(x[0]))
                ?? "review";
            slug = $"{facetToken[..Math.Min(facetToken.Length, 30)]}-finding-{ordinal}";
        }
        return slug;
    }
}
