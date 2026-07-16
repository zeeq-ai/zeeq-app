namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// A caller-facing request to <see cref="SnippetSearchService.SearchAsync"/>.
/// </summary>
/// <remarks>
/// Mirrors the shape MCP tools and the HTTP search endpoint both need to build: an org resolved
/// from claims (never caller input), a single required library name (no "all libraries" mode —
/// see <see cref="SnippetSearchService"/>'s remarks), and the raw, unnormalized caller inputs.
/// Normalization (excluded paths), clamping (<see cref="MaxResults"/>), and identifier extraction
/// all happen inside the service so every caller gets identical behavior.
/// </remarks>
/// <param name="OrganizationId">The searching org, resolved from claims by the caller.</param>
/// <param name="LibraryName">The single required library name to search within.</param>
/// <param name="Kind">Section or code — search is always scoped to one kind.</param>
/// <param name="Query">The caller's search text (websearch syntax for the full-text arm).</param>
/// <param name="ExcludedDocumentPaths">Optional document paths to exclude (e.g. already read).</param>
/// <param name="MaxResults">Optional result cap; defaults to 5, clamped to 1..15.</param>
public sealed record SnippetSearchRequest(
    string OrganizationId,
    string LibraryName,
    SnippetKind Kind,
    string Query,
    string[]? ExcludedDocumentPaths,
    int? MaxResults
);

/// <summary>
/// The result of a successful <see cref="SnippetSearchService.SearchAsync"/> call.
/// </summary>
/// <remarks>
/// Carries the resolved library's display name alongside the ranked rows so callers can format
/// source pointers (e.g. "library: {LibraryName}") without a second library lookup.
/// </remarks>
/// <param name="LibraryName">The resolved library's display name, echoed back to the caller.</param>
/// <param name="Degraded">
/// True when the query embedding could not be generated (provider failure or timeout) and the
/// search ran full-text-only.
/// </param>
/// <param name="Rows">The ranked snippet rows.</param>
public sealed record SnippetSearchResult(
    string LibraryName,
    bool Degraded,
    IReadOnlyList<SnippetSearchRow> Rows
);

/// <summary>
/// The outcome of a <see cref="SnippetSearchService.SearchAsync"/> call: either a
/// <see cref="Result"/> or a caller-facing <see cref="Error"/>, never both.
/// </summary>
/// <remarks>
/// Mirrors <c>DocumentLibraryMcpTools.ResolvedLibrary</c>'s error-carrying pattern: an unknown or
/// missing library name is a validation error the caller must see, not an empty result set.
/// <see cref="ErrorKind"/> exists so HTTP callers (unlike MCP tools, which just return the message
/// text either way) can map a validation failure to 400 and a not-found library to 404 — the same
/// split <c>DocumentEndpointProblemKind</c> already makes for the plain document endpoints.
/// </remarks>
/// <param name="Result">The search result when the request succeeds.</param>
/// <param name="Error">A caller-facing validation or not-found message when the request fails.</param>
/// <param name="ErrorKind">Which kind of failure <see cref="Error"/> represents; null on success.</param>
public sealed record SnippetSearchOutcome(
    SnippetSearchResult? Result,
    string? Error,
    SnippetSearchErrorKind? ErrorKind = null
);

/// <summary>
/// Distinguishes a <see cref="SnippetSearchOutcome.Error"/>'s cause for callers that need to map it
/// to a status code (e.g. an HTTP endpoint) rather than just surface the message text.
/// </summary>
public enum SnippetSearchErrorKind
{
    /// <summary>Missing/blank org, library name, or query — a caller input problem.</summary>
    Validation,

    /// <summary>The named library does not exist in the org.</summary>
    NotFound,
}
