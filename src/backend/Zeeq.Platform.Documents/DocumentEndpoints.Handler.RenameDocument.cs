using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Renames (moves) a document to a new path within the same library (D-3, D-4).
/// </summary>
/// <remarks>
/// <para>
/// Delegates the move and alias bookkeeping to <see cref="ILibraryDocumentStore.MoveDocumentAsync"/>.
/// The store appends the old path to <c>PreviousPaths</c> so old links/references still resolve.
/// A 409 is returned when the target path collides with an existing live path or previous-path alias.
/// </para>
/// <para>
/// The returned <see cref="DocumentResponse"/> reflects the new path and preserves the original
/// <c>Id</c> and <c>CreatedAt</c>.
/// </para>
/// </remarks>
public sealed class RenameDocumentHandler(ILibraryDocumentStore store) : IEndpointHandler
{
    /// <summary>
    /// Handles the rename document request.
    /// </summary>
    /// <param name="orgId">Organization ID from the route.</param>
    /// <param name="name">Library name.</param>
    /// <param name="request">The rename request with from/to paths.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The moved document summary or a 400/404/409.</returns>
    public async Task<
        Results<Ok<DocumentResponse>, BadRequest<DocumentError>, NotFound, Conflict<DocumentError>>
    > HandleAsync(
        string orgId,
        string name,
        RenameDocumentRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.FromPath))
        {
            return TypedResults.BadRequest(new DocumentError("fromPath is required."));
        }

        if (string.IsNullOrWhiteSpace(request.ToPath))
        {
            return TypedResults.BadRequest(new DocumentError("toPath is required."));
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
            // MoveDocumentAsync returns null when the source path does not resolve.
            var moved = await store.MoveDocumentAsync(
                context.OrganizationId,
                context.Library!.Id,
                request.FromPath,
                request.ToPath,
                ct
            );

            return moved is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(LibraryEndpointMapping.ToResponse(moved));
        }
        catch (ArgumentException ex)
        {
            // Bad normalized target path.
            return TypedResults.BadRequest(new DocumentError(ex.Message));
        }
        catch (DuplicateDocumentPathException ex)
        {
            // Target already occupied (existing live path or alias).
            return TypedResults.Conflict(new DocumentError(ex.Message));
        }
    }
}
