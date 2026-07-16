using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Writes markdown documents into a library.
/// </summary>
/// <remarks>
/// This handler is intentionally thin: it validates the HTTP contract, resolves the library name
/// to a stable library id, and delegates parsing, normalization, hashing, token counting, and
/// persistence semantics to <see cref="LibraryDocumentWriteService"/>. The write service treats
/// unchanged content as a no-op and keeps an existing document's team ownership immutable.
/// </remarks>
public sealed class UpsertDocumentHandler(
    ILibraryDocumentStore store,
    LibraryDocumentWriteService writer
) : IEndpointHandler
{
    /// <summary>
    /// Handles the upsert document request.
    /// </summary>
    /// <remarks>
    /// The request path is supplied in the body so nested markdown paths remain opaque data rather
    /// than route structure. Invalid normalized paths, including <c>.</c> or <c>..</c> segments,
    /// are reported as <c>400 Bad Request</c> from the domain normalizer.
    /// </remarks>
    public async Task<
        Results<Ok<DocumentResponse>, BadRequest<DocumentError>, NotFound>
    > HandleAsync(
        string orgId,
        string name,
        UpsertDocumentRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return TypedResults.BadRequest(new DocumentError("Document path is required."));
        }

        if (request.Content is null)
        {
            return TypedResults.BadRequest(new DocumentError("Document content is required."));
        }

        var context = await DocumentEndpointContext.ResolveAsync(store, orgId, name, ct);
        if (context.Problem is not null)
        {
            return context.Problem.Kind == DocumentEndpointProblemKind.NotFound
                ? TypedResults.NotFound()
                : TypedResults.BadRequest(new DocumentError(context.Problem.Message!));
        }

        try
        {
            // NOTE: The write service owns markdown parsing and the content-hash no-op check.
            // Keep endpoint behavior limited to HTTP validation and library-name resolution.
            var document = await writer.UpsertDocumentAsync(
                context.OrganizationId,
                user.AsZeeqMinimalIdentity().TeamId,
                context.Library!.Id,
                request.Path,
                request.Content,
                ct
            );

            return TypedResults.Ok(LibraryEndpointMapping.ToResponse(document));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new DocumentError(ex.Message));
        }
    }
}
