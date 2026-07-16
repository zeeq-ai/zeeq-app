using Pgvector;

namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// A hybrid (RRF-fused vector + full-text) snippet search request against exactly one store.
/// </summary>
/// <remarks>
/// <see cref="ISnippetStore{TDocument}"/> is one generic contract shared by the private
/// (<see cref="LibraryDocumentSnippet"/>) and public (<see cref="PublicDocumentSnippet"/>) stores,
/// so this query carries both stores' possible scope fields — each implementation reads only the
/// ones it needs and ignores the other store's fields. Callers always resolve to exactly one
/// store per search (a library is either privately or publicly sourced, never both), so exactly
/// one of the scope pairs below is populated at any call site.
/// </remarks>
/// <param name="OrganizationId">
/// Private store only: the searching org (claims-derived, never caller input).
/// </param>
/// <param name="LibraryId">Private store only: the single required library id.</param>
/// <param name="PublicSourceId">Public store only: the subscribed public source id.</param>
/// <param name="Kind">Section or code — search is always scoped to one kind.</param>
/// <param name="QueryText">Websearch-syntax text for the full-text arm.</param>
/// <param name="QueryEmbedding">
/// The instruction-prefixed query embedding for the vector arm; null runs full-text-only
/// (degraded mode, e.g. when the embedding provider is unavailable or times out).
/// <see cref="HalfVector"/> to match the <c>halfvec(768)</c> storage/index type exactly.
/// </param>
/// <param name="QueryIdentifiers">
/// Lowercased identifiers extracted from the query text; snippets whose <c>Identifiers</c>
/// overlap get an exact-match ranking boost.
/// </param>
/// <param name="ExcludedDocumentPaths">
/// Normalized document paths to exclude (e.g. documents the caller already read), applied in
/// both arms before fusion.
/// </param>
/// <param name="Limit">Maximum results to return.</param>
public sealed record SnippetSearchQuery(
    string? OrganizationId,
    string? LibraryId,
    string? PublicSourceId,
    SnippetKind Kind,
    string QueryText,
    HalfVector? QueryEmbedding,
    string[] QueryIdentifiers,
    string[] ExcludedDocumentPaths,
    int Limit
);
