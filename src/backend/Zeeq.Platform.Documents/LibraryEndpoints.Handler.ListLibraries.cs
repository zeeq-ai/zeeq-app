using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Lists libraries in the caller's active organization.
/// </summary>
public sealed class ListLibrariesHandler(
    ILibraryVisibilityStore visibility,
    IDocsPublicSourceStore publicSources
) : IEndpointHandler
{
    /// <summary>
    /// Handles the list libraries request.
    /// </summary>
    public async Task<Results<Ok<LibraryResponse[]>, BadRequest<LibraryError>>> HandleAsync(
        string orgId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var audience = user.GetLibraryAudience(orgId);
        if (audience is null)
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        var libraries = await visibility.ListVisibleAsync(audience, ct);

        // One batch query for every distinct public source referenced, rather
        // than one per library.
        var publicSourceIds = libraries
            .Select(library => library.PublicSourceId)
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct()
            .ToArray();
        var sourcesById =
            publicSourceIds.Length == 0
                ? LibraryEndpointMapping.NoPublicSources
                : (await publicSources.GetByIdsAsync(publicSourceIds, ct)).ToDictionary(source =>
                    source.Id
                );

        return TypedResults.Ok(
            libraries
                .Select(library => LibraryEndpointMapping.ToResponse(library, sourcesById))
                .ToArray()
        );
    }
}
