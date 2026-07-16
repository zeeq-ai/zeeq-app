using Zeeq.Core.Documents;
using Zeeq.Platform.Ingest;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Lists documents in a library.
/// </summary>
/// <remarks>
/// Branches on whether the library is public-source-backed: its documents
/// live in the shared, global <c>docs_public_documents</c> table (via
/// <see cref="IDocsPublicDocumentStore"/>), not the org-scoped
/// <c>docs_library_documents</c> table <see cref="ILibraryDocumentStore"/>'s
/// document methods read. Since that shared table holds the union of every
/// subscribing library's scope, this re-applies the library's own effective
/// filter at query time (<see cref="LibraryEndpointMapping.ResolveEffectiveFilter"/>)
/// so one org never sees another org's wider-filtered files.
/// <c>IngestFileFilter</c> physically lives in <c>Zeeq.Core.Documents</c>
/// now but keeps its original <c>Zeeq.Platform.Ingest</c> namespace (same
/// relocation pattern as <c>ICodeRepositoryStore</c>/<c>Zeeq.Platform.CodeReviews</c>)
/// — <c>Zeeq.Platform.Documents</c> cannot reference <c>Zeeq.Platform.Ingest</c>
/// itself (circular reference), but the type is reachable because it's
/// physically in an already-referenced project.
/// </remarks>
public sealed class ListDocumentsHandler(
    ILibraryDocumentStore store,
    IDocsPublicDocumentStore publicDocuments,
    IDocsPublicSourceStore publicSources
) : IEndpointHandler
{
    /// <summary>
    /// Handles the list documents request.
    /// </summary>
    public async Task<
        Results<Ok<DocumentResponse[]>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(string orgId, string name, CancellationToken ct)
    {
        var context = await DocumentEndpointContext.ResolveAsync(store, orgId, name, ct);
        if (context.Problem is not null)
        {
            return context.Problem.Kind == DocumentEndpointProblemKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.BadRequest(new DocumentError(context.Problem.Message!));
        }

        var library = context.Library!;
        if (library.PublicSourceId is { } publicSourceId)
        {
            var source = await publicSources.GetByIdAsync(publicSourceId, ct);
            if (source is null)
            {
                return TypedResults.NotFound();
            }

            var filter = LibraryEndpointMapping.ResolveEffectiveFilter(library, source);
            var allDocuments = await publicDocuments.ListSummariesBySourceAsync(publicSourceId, ct);
            var visible = allDocuments.Where(document =>
                IngestFileFilter.IsIncluded(document.Path, filter)
            );

            return TypedResults.Ok(visible.Select(LibraryEndpointMapping.ToResponse).ToArray());
        }

        var documents = await store.ListDocumentsAsync(context.OrganizationId, library.Id, ct);

        return TypedResults.Ok(documents.Select(LibraryEndpointMapping.ToResponse).ToArray());
    }
}
