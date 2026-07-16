using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Gets a library by name from the caller's active organization.
/// </summary>
public sealed class GetLibraryHandler(
    ILibraryDocumentStore store,
    IDocsPublicSourceStore publicSources
) : IEndpointHandler
{
    /// <summary>
    /// Handles the get library request.
    /// </summary>
    public async Task<Results<Ok<LibraryResponse>, BadRequest<LibraryError>, NotFound>> HandleAsync(
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

        var sourcesById = await LibraryEndpointMapping.LoadPublicSourceAsync(
            publicSources,
            library,
            ct
        );
        return TypedResults.Ok(LibraryEndpointMapping.ToResponse(library, sourcesById));
    }
}
