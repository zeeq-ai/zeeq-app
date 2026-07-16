using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Searches documents in a library by keywords, returning ranked results for the "Test" panel.
/// </summary>
/// <remarks>
/// <para>
/// The store's <see cref="ILibraryDocumentStore.SearchAsync"/> runs combined full-text and fuzzy
/// retrieval. Each result carries its match type and per-signal scores so the UI can display
/// transparent ranking hints.
/// </para>
/// <para>
/// (D-1) The resolved library name is echoed onto every result row so the front-end can display
/// the library per row without a separate prop.
/// </para>
/// </remarks>
public sealed class SearchDocumentsHandler(ILibraryDocumentStore store) : IEndpointHandler
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    /// <summary>
    /// Handles the search documents request.
    /// </summary>
    /// <param name="orgId">Organization ID from the route.</param>
    /// <param name="name">Library name.</param>
    /// <param name="query">Search keywords.</param>
    /// <param name="limit">Optional result cap, clamped to [1, 50].</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ranked search results or a 400/404.</returns>
    public async Task<
        Results<Ok<DocumentSearchResultResponse[]>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        string query,
        int? limit,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return TypedResults.BadRequest(new DocumentError("Search query is required."));
        }

        var context = await DocumentEndpointContext.ResolveAsync(store, orgId, name, ct);
        if (context.Problem is not null)
        {
            return context.Problem.Kind == DocumentEndpointProblemKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.BadRequest(new DocumentError(context.Problem.Message!));
        }

        var capped = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var matches = await store.SearchAsync(
            context.OrganizationId,
            context.Library!.Id,
            query,
            capped,
            ct
        );

        // (D-1) Echo the resolved library name onto each hit.
        return TypedResults.Ok(
            matches
                .Select(m => LibraryEndpointMapping.ToSearchResult(m, context.Library!.Name))
                .ToArray()
        );
    }
}
