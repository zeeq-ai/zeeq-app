namespace Zeeq.Core.Documents;

/// <summary>
/// Identifies which retrieval signal matched a document during a combined search.
/// </summary>
/// <remarks>
/// A single query runs full-text and fuzzy-title retrieval together. The match type tells callers
/// why a document surfaced so an agent can weigh a strong content hit against a title-spelling guess.
/// </remarks>
public enum DocumentMatchType
{
    /// <summary>The websearch full-text query matched the document's search vector.</summary>
    FullText,

    /// <summary>Only the trigram title similarity matched; the full-text query did not.</summary>
    Fuzzy,

    /// <summary>Both the full-text query and the trigram title similarity matched.</summary>
    Both,
}

/// <summary>
/// A document returned by combined full-text and fuzzy search, with its per-signal scores.
/// </summary>
/// <remarks>
/// Both scores are bounded to a comparable range so callers can read them directly. Results are
/// ordered server-side so that any full-text hit outranks a fuzzy-only hit; the scores are exposed
/// for transparency rather than re-ranking.
/// </remarks>
/// <param name="Document">The matched document.</param>
/// <param name="MatchType">Which retrieval signal(s) matched.</param>
/// <param name="FullTextScore">
/// Normalized <c>ts_rank_cd</c> in the range [0, 1); 0 when the full-text query did not match.
/// </param>
/// <param name="FuzzyScore">
/// Trigram title similarity in the range [0, 1]; 0 when the fuzzy title search did not match.
/// </param>
public sealed record LibraryDocumentMatch(
    LibraryDocument Document,
    DocumentMatchType MatchType,
    double FullTextScore,
    double FuzzyScore
);
