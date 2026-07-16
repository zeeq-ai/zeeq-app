using Zeeq.Core.Documents;
using Zeeq.Platform.Ingest;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Discriminates the outcome of <see cref="DocumentContentResolvingHandler.ResolveContentAsync"/>.
/// </summary>
public enum DocumentResolutionKind
{
    /// <summary>The document was resolved; <see cref="DocumentResolution.Content"/> is populated.</summary>
    Found,

    /// <summary>The request itself was invalid (e.g. missing path).</summary>
    BadRequest,

    /// <summary>No document matched, or it fell outside the library's effective filter scope.</summary>
    NotFound,
}

/// <summary>
/// Result of resolving a document's path/content for one org/library/path — either the resolved
/// document (with whichever of <see cref="LocalDocument"/>/<see cref="PublicDocument"/> applies,
/// so a caller needing the full entity for its own response mapping still has it), or a
/// bad-request/not-found outcome ready to translate into the caller's own <c>Results&lt;...&gt;</c>
/// union.
/// </summary>
public sealed record DocumentResolution(
    DocumentResolutionKind Kind,
    string? ResolvedPath,
    string? Content,
    LibraryDocument? LocalDocument,
    DocsPublicDocument? PublicDocument,
    string? ErrorMessage
)
{
    /// <summary>Builds a <see cref="DocumentResolutionKind.Found"/> result.</summary>
    public static DocumentResolution Found(
        string resolvedPath,
        string content,
        LibraryDocument? localDocument,
        DocsPublicDocument? publicDocument
    ) => new(DocumentResolutionKind.Found, resolvedPath, content, localDocument, publicDocument, null);

    /// <summary>Builds a <see cref="DocumentResolutionKind.NotFound"/> result.</summary>
    public static DocumentResolution NotFound() =>
        new(DocumentResolutionKind.NotFound, null, null, null, null, null);

    /// <summary>Builds a <see cref="DocumentResolutionKind.BadRequest"/> result.</summary>
    public static DocumentResolution BadRequest(string message) =>
        new(DocumentResolutionKind.BadRequest, null, null, null, null, message);
}

/// <summary>
/// Shared base for endpoint handlers that need a document's resolved path and full content,
/// branching on public-source vs. private/local library the same way regardless of what the
/// concrete handler does with that content afterward — return it raw
/// (<see cref="GetDocumentContentHandler"/>) or parse/compose it
/// (<see cref="PreviewDocumentParseHandler"/>).
/// </summary>
/// <remarks>
/// The effective-filter re-check in <see cref="ResolveContentAsync"/> is not optional: without
/// it, a caller could reach a document outside their library's own scope by path even though the
/// list endpoint would never surface it. Centralizing it here means that check can only be fixed
/// or broken once, not once per handler that happens to also need document content.
/// </remarks>
public abstract class DocumentContentResolvingHandler(
    ILibraryDocumentStore store,
    IDocsPublicDocumentStore publicDocuments,
    IDocsPublicSourceStore publicSources
)
{
    /// <summary>Resolves a document's path and content for one org/library/path.</summary>
    protected async Task<DocumentResolution> ResolveContentAsync(
        string orgId,
        string name,
        string path,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DocumentResolution.BadRequest("Document path is required.");
        }

        var context = await DocumentEndpointContext.ResolveAsync(store, orgId, name, ct);
        if (context.Problem is not null)
        {
            return context.Problem.Kind == DocumentEndpointProblemKind.NotFound
                ? DocumentResolution.NotFound()
                : DocumentResolution.BadRequest(context.Problem.Message!);
        }

        var library = context.Library!;
        if (library.PublicSourceId is { } publicSourceId)
        {
            var source = await publicSources.GetByIdAsync(publicSourceId, ct);
            if (source is null)
            {
                return DocumentResolution.NotFound();
            }

            var doc = await publicDocuments.GetByPathAsync(publicSourceId, path, ct);
            if (doc is null)
            {
                return DocumentResolution.NotFound();
            }

            var filter = LibraryEndpointMapping.ResolveEffectiveFilter(library, source);
            if (!IngestFileFilter.IsIncluded(doc.Path, filter))
            {
                return DocumentResolution.NotFound();
            }

            return DocumentResolution.Found(doc.Path, doc.Content, localDocument: null, publicDocument: doc);
        }

        var localDoc = await store.GetByPathAsync(context.OrganizationId, library.Id, path, ct);

        return localDoc is null
            ? DocumentResolution.NotFound()
            : DocumentResolution.Found(
                localDoc.Path,
                localDoc.Content,
                localDocument: localDoc,
                publicDocument: null
            );
    }
}
