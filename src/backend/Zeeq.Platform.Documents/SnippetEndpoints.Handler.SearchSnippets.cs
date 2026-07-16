using Zeeq.Core.Documents.Snippets;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Searches section or code snippets in a library by hybrid (vector + full-text) ranking, for the
/// "Test" panel's Sections/Code modes.
/// </summary>
/// <remarks>
/// Unlike the MCP <c>search_sections</c>/<c>search_code_snippets</c> tools (markdown output, no
/// score components), this endpoint exists to test and tune ranking — it exposes the raw
/// <see cref="SnippetSearchRow"/> score components (<c>score</c>, <c>vectorRank</c>,
/// <c>textRank</c>, <c>identifierMatch</c>) directly. Org and library both come from the route
/// (already validated against the auth cookie by <c>RequireRouteOrganizationMatchesCookie</c>, same
/// as every other library endpoint), so <see cref="SnippetSearchService"/> does the rest of its own
/// validation and not-found handling from there.
/// </remarks>
public sealed class SearchSnippetsHandler(SnippetSearchService searchService) : IEndpointHandler
{
    /// <summary>
    /// Handles the search snippets request.
    /// </summary>
    /// <param name="orgId">Organization ID from the route.</param>
    /// <param name="name">Library name from the route.</param>
    /// <param name="kind">"section" or "code", case-insensitive.</param>
    /// <param name="query">Search text (full-text arm) and query-embedding source (vector arm).</param>
    /// <param name="excludeDocumentPaths">Optional document paths to exclude.</param>
    /// <param name="maxResults">Optional result cap, clamped to 1..15 by the service.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked snippet results, or a 400/404.</returns>
    public async Task<
        Results<Ok<SnippetSearchResultResponse[]>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        string kind,
        string query,
        string[]? excludeDocumentPaths,
        int? maxResults,
        CancellationToken ct
    )
    {
        if (!TryParseKind(kind, out var snippetKind))
        {
            return TypedResults.BadRequest(
                new DocumentError($"kind must be 'section' or 'code'; got '{kind}'.")
            );
        }

        var outcome = await searchService.SearchAsync(
            new SnippetSearchRequest(
                orgId,
                name,
                snippetKind,
                query,
                excludeDocumentPaths,
                maxResults
            ),
            ct
        );

        if (outcome.Error is not null)
        {
            return outcome.ErrorKind == SnippetSearchErrorKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.BadRequest(new DocumentError(outcome.Error));
        }

        var result = outcome.Result!;

        return TypedResults.Ok(
            result
                .Rows.Select(row => new SnippetSearchResultResponse(
                    row.DocumentPath,
                    row.DocumentTitle,
                    row.HeadingPath,
                    row.Header,
                    row.Language,
                    row.Content,
                    row.Score,
                    row.VectorRank,
                    row.TextRank,
                    row.IdentifierMatch,
                    result.Degraded
                ))
                .ToArray()
        );
    }

    private static bool TryParseKind(string kind, out SnippetKind snippetKind)
    {
        // NOTE: intentionally nullable here, not a bare switch expression defaulting to
        // `default` — SnippetKind.Section is enum value 0, the same as default(SnippetKind), so
        // matching on `snippetKind is Section or Code` after a `_ => default` arm would silently
        // accept an invalid kind string as Section instead of rejecting it (caught before this
        // shipped, code review follow-up 2026-07-11).
        SnippetKind? parsed = kind.ToLowerInvariant() switch
        {
            "section" => SnippetKind.Section,
            "code" => SnippetKind.Code,
            _ => null,
        };

        snippetKind = parsed ?? default;

        return parsed is not null;
    }
}
