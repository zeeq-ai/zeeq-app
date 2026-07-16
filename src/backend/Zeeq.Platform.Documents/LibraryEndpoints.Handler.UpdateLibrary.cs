using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Updates a library in the caller's active organization.
/// </summary>
public sealed class UpdateLibraryHandler(
    ILibraryDocumentStore store,
    IDocsPublicSourceStore publicSources
) : IEndpointHandler
{
    /// <summary>
    /// Handles the update library request.
    /// </summary>
    public async Task<Results<Ok<LibraryResponse>, BadRequest<LibraryError>, NotFound>> HandleAsync(
        string orgId,
        string name,
        UpdateLibraryRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return TypedResults.BadRequest(new LibraryError("Active organization is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.BadRequest(new LibraryError("Library name is required."));
        }

        var newName = request.Name.Trim();
        if (!LibraryNameValidator.IsRouteSafe(newName))
        {
            return TypedResults.BadRequest(
                new LibraryError(
                    "Library name must contain only letters, numbers, hyphens, and underscores."
                )
            );
        }

        var existing = await store.GetLibraryAsync(orgId, name, ct);
        if (existing is null)
        {
            return TypedResults.NotFound();
        }

        var updated = await store.UpdateLibraryAsync(
            new Library
            {
                Id = existing.Id,
                OrganizationId = existing.OrganizationId,
                TeamId = existing.TeamId,
                Name = newName,
                Description = request.Description ?? existing.Description,
                PublicSourceId = existing.PublicSourceId,
                IncludeFilters = request.IncludeFilters ?? existing.IncludeFilters,
                ExcludeFilters = request.ExcludeFilters ?? existing.ExcludeFilters,
                SourceKind = existing.SourceKind,
                SourceRepoUrl = existing.SourceRepoUrl,
                SourceSyncedAt = existing.SourceSyncedAt,
                SourceDefaultIncludeFilters = existing.SourceDefaultIncludeFilters,
                SourceDefaultExcludeFilters = existing.SourceDefaultExcludeFilters,
                SyncStatus = existing.SyncStatus,
                NextSyncAt = existing.NextSyncAt,
                ManualTriggerHistory = existing.ManualTriggerHistory,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            ct
        );

        var sourcesById = await LibraryEndpointMapping.LoadPublicSourceAsync(
            publicSources,
            updated,
            ct
        );
        return TypedResults.Ok(LibraryEndpointMapping.ToResponse(updated, sourcesById));
    }
}
