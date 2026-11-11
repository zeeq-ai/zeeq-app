using System.Text.RegularExpressions;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>Prepares a forgiving full-text query for snippet retrieval.</summary>
internal static class SnippetFullTextQuery
{
    /// <summary>Matches any term in plain queries while preserving explicit web-search syntax.</summary>
    public static string Prepare(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // Preserve phrases, exclusions and explicit OR for callers using web-search syntax.
        if (
            query.Contains('"')
            || Regex.IsMatch(query, @"(?<![\p{L}\p{N}_])-(?=[\p{L}\p{N}_])")
            || terms.Any(term =>
                term.StartsWith('-') || term.Equals("OR", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return query;
        }

        // websearch_to_tsquery safely handles punctuation and stop words; OR lets a section
        // match even when a long natural-language query contains words found in other sections.
        var words = Regex.Split(query, @"[^\p{L}\p{N}_]+").Where(word => word.Length > 0);

        return string.Join(" OR ", words);
    }
}
