namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// One ranked result row from <see cref="ISnippetStore{TDocument}.SearchAsync"/>.
/// </summary>
/// <remarks>
/// <see cref="Score"/> is the Reciprocal Rank Fusion score (<c>1/(k+vectorRank) +
/// 1/(k+textRank)</c>, plus the identifier boost), comparable across the private and public
/// stores since both use the same rank-based formula — a caller merging rows from two stores can
/// sort by <see cref="Score"/> directly. Document path/title are resolved in the same query at
/// read time, so a renamed document never invalidates a stored snippet.
/// </remarks>
/// <param name="SnippetId">Stable snippet identifier.</param>
/// <param name="DocumentId">FK to the owning document.</param>
/// <param name="DocumentPath">The owning document's current normalized path.</param>
/// <param name="DocumentTitle">The owning document's current title.</param>
/// <param name="Header">Heading text that owns this snippet.</param>
/// <param name="HeadingPath">Hierarchical heading path.</param>
/// <param name="Language">Fence language (code only), else null.</param>
/// <param name="Tag">Resolved fence tag (code only), else null.</param>
/// <param name="Content">The snippet body.</param>
/// <param name="TokenCount">Token count of the embedding payload.</param>
/// <param name="Score">Fused RRF score (plus identifier boost); higher ranks first.</param>
/// <param name="VectorRank">1-based rank in the HNSW vector arm; 0 if not a vector-arm hit.</param>
/// <param name="TextRank">1-based rank in the FTS arm; 0 if not a full-text-arm hit.</param>
/// <param name="IdentifierMatch">Whether the query's extracted identifiers overlapped this row's.</param>
public sealed record SnippetSearchRow(
    string SnippetId,
    string DocumentId,
    string DocumentPath,
    string DocumentTitle,
    string Header,
    string HeadingPath,
    string? Language,
    string? Tag,
    string Content,
    int TokenCount,
    double Score,
    int VectorRank,
    int TextRank,
    bool IdentifierMatch
);
