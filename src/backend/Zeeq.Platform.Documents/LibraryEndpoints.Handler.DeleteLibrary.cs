using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Deletes a library from the caller's active organization.
/// </summary>
public sealed class DeleteLibraryHandler(ILibraryDocumentStore store) : IEndpointHandler
{
    /// <summary>
    /// Handles the delete library request.
    /// </summary>
    public async Task<Results<NoContent, BadRequest<LibraryError>, NotFound>> HandleAsync(
        string orgId,
        string name,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        var library = await store.GetLibraryAsync(orgId, name, ct);
        if (library is null)
        {
            return TypedResults.NotFound();
        }

        await store.DeleteLibraryAsync(orgId, library.Id, ct);

        return TypedResults.NoContent();
    }
}
