using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Loads a document's full markdown content by path for the editor.
/// </summary>
/// <remarks>
/// This is intentionally separate from the summary list endpoint because the content body is large
/// and unnecessary for tree/list rendering. The store's <see cref="ILibraryDocumentStore.GetByPathAsync"/>
/// does tiered exact→suffix→filename resolution and is <c>HybridCache</c>-backed.
/// <para>
/// Branches on public-source vs. private/local the same way <see cref="ListDocumentsHandler"/> and
/// <see cref="PreviewDocumentParseHandler"/> do — the shared branching plus effective-filter
/// re-check lives in <see cref="DocumentContentResolvingHandler.ResolveContentAsync"/>.
/// </para>
/// </remarks>
public sealed class GetDocumentContentHandler(
    ILibraryDocumentStore store,
    IDocsPublicDocumentStore publicDocuments,
    IDocsPublicSourceStore publicSources
) : DocumentContentResolvingHandler(store, publicDocuments, publicSources), IEndpointHandler
{
    /// <summary>
    /// Handles the get document content request.
    /// </summary>
    /// <param name="orgId">Organization ID from the route.</param>
    /// <param name="name">Library name.</param>
    /// <param name="path">Document path query parameter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full document response or a 400/404.</returns>
    public async Task<
        Results<Ok<DocumentContentResponse>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(string orgId, string name, string path, CancellationToken ct)
    {
        var resolution = await ResolveContentAsync(orgId, name, path, ct);

        return resolution.Kind switch
        {
            DocumentResolutionKind.BadRequest => TypedResults.BadRequest(
                new DocumentError(resolution.ErrorMessage!)
            ),
            DocumentResolutionKind.NotFound => TypedResults.NotFound(),
            _ => TypedResults.Ok(
                resolution.PublicDocument is not null
                    ? LibraryEndpointMapping.ToContentResponse(resolution.PublicDocument)
                    : LibraryEndpointMapping.ToContentResponse(resolution.LocalDocument!)
            ),
        };
    }
}
