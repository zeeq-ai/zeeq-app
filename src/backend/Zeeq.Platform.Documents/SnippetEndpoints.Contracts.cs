namespace Zeeq.Platform.Documents;

/// <summary>
/// A ranked snippet search hit for the "Test" panel's Sections/Code modes.
/// </summary>
/// <remarks>
/// No <c>library</c> field — unlike <c>DocumentSearchResultResponse</c>, this endpoint is already
/// scoped to exactly one library by the route (<c>orgs/{orgId}/libraries/{name}/snippets/search</c>),
/// so echoing it per row would be redundant. <see cref="Degraded"/> is still echoed per row (same
/// "no wrapper object" convention as that response's per-row <c>Library</c> field) since every row
/// from one search call shares the same degraded-mode value and the UI needs it alongside the rows
/// it's rendering, not in a separate envelope.
/// </remarks>
/// <param name="DocumentPath">The owning document's current normalized path.</param>
/// <param name="DocumentTitle">The owning document's current title.</param>
/// <param name="HeadingPath">Hierarchical heading path (breadcrumb style, e.g. "Guide &gt; Install &gt; Linux").</param>
/// <param name="Header">Heading text that owns this snippet.</param>
/// <param name="Language">Fence language (code snippets only); null for section snippets.</param>
/// <param name="Content">The snippet body.</param>
/// <param name="Score">Fused RRF score (plus identifier boost); higher ranks first.</param>
/// <param name="VectorRank">1-based rank in the HNSW vector arm; 0 if not a vector-arm hit.</param>
/// <param name="TextRank">1-based rank in the FTS arm; 0 if not a full-text-arm hit.</param>
/// <param name="IdentifierMatch">Whether the query's extracted identifiers overlapped this row's.</param>
/// <param name="Degraded">True when semantic ranking was unavailable and this row is FTS-only.</param>
public sealed record SnippetSearchResultResponse(
    string DocumentPath,
    string DocumentTitle,
    string HeadingPath,
    string Header,
    string? Language,
    string Content,
    double Score,
    int VectorRank,
    int TextRank,
    bool IdentifierMatch,
    bool Degraded
);
