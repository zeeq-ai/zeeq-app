using Zeeq.Core.Documents;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Deletes documents from a library by path.
/// </summary>
public sealed class DeleteDocumentHandler(ILibraryDocumentStore store) : IEndpointHandler
{
    /// <summary>
    /// Handles the delete document request.
    /// </summary>
    public async Task<Results<NoContent, BadRequest<DocumentError>, NotFound>> HandleAsync(
        string orgId,
        string name,
        string path,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return TypedResults.BadRequest(new DocumentError("Document path is required."));
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
            var normalizedPath = DocumentNormalizer.NormalizePath(path);
            await store.DeleteDocumentAsync(
                context.OrganizationId,
                context.Library!.Id,
                normalizedPath,
                ct
            );
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new DocumentError(ex.Message));
        }

        return TypedResults.NoContent();
    }
}
