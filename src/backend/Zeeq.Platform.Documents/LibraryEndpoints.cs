using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Browser-authenticated endpoints for managing manual document libraries.
/// </summary>
public sealed class LibraryEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/libraries")
            .WithTags("Libraries")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}/libraries
        group
            .MapGet(
                "/",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListLibrariesHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("ListLibraries")
            .Produces<LibraryResponse[]>()
            .Produces<LibraryError>(StatusCodes.Status400BadRequest)
            .WithSummary("List libraries.")
            .WithDescription(
                """
                Returns all document libraries in the caller's active organization, ordered by name.
                """
            )
            .RequireActiveOrganization();

        // POST /api/v1/orgs/{orgId}/libraries
        group
            .MapPost(
                "/",
                static (
                    string orgId,
                    CreateLibraryRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateLibraryHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .WithName("CreateLibrary")
            .Produces<LibraryResponse>(StatusCodes.Status201Created)
            .Produces<LibraryError>(StatusCodes.Status400BadRequest)
            .WithSummary("Create a library.")
            .WithDescription(
                """
                Creates a new document library in the caller's active organization.

                The name must be unique within the organization and contain only letters, numbers,
                hyphens, and underscores — it doubles as the URL segment used to address the library
                in subsequent requests.

                Pass `source` to create a repository-sourced library instead of a plain
                hand-authored one. `source.kind = Public` requires `source.repoUrl` (a raw
                `https://github.com/owner/repo` clone URL); `source.kind = Private` requires
                `source.repositoryId`, an id from this organization's configured GitHub
                repositories. Returns `404` when `source.repositoryId` doesn't resolve to a
                configured repository. Creating a repository-sourced library does not queue its
                initial sync — call `POST .../libraries/{name}/ingest-run` immediately after.
                """
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/libraries/{name}
        group
            .MapGet(
                "/{name}",
                static (
                    string orgId,
                    string name,
                    ClaimsPrincipal user,
                    [FromServices] GetLibraryHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, user, ct)
            )
            .WithName("GetLibrary")
            .Produces<LibraryResponse>()
            .Produces<LibraryError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get a library.")
            .WithDescription(
                """
                Returns the library identified by `name` in the caller's active organization.
                """
            )
            .RequireActiveOrganization();

        // PUT /api/v1/orgs/{orgId}/libraries/{name}
        group
            .MapPut(
                "/{name}",
                static (
                    string orgId,
                    string name,
                    UpdateLibraryRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateLibraryHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, request, user, ct)
            )
            .WithName("UpdateLibrary")
            .Produces<LibraryResponse>()
            .Produces<LibraryError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Update a library.")
            .WithDescription(
                """
                Updates the name or description of an existing library identified by `name`.

                Renaming a library changes the URL segment used to address it; subsequent requests
                must use the new name.
                """
            )
            .RequireActiveOrganization();

        // DELETE /api/v1/orgs/{orgId}/libraries/{name}
        group
            .MapDelete(
                "/{name}",
                static (
                    string orgId,
                    string name,
                    ClaimsPrincipal user,
                    [FromServices] DeleteLibraryHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, user, ct)
            )
            .WithName("DeleteLibrary")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<LibraryError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Delete a library.")
            .WithDescription(
                """
                Permanently deletes the library and all its documents. Also removes this library's
                ID from any repository mappings that referenced it. This operation cannot be undone.
                """
            )
            .RequireActiveOrganization();

        // PUT /api/v1/orgs/{orgId}/libraries/{name}/repositories
        group
            .MapPut(
                "/{name}/repositories",
                static (
                    string orgId,
                    string name,
                    UpdateLibraryRepositoryMappingsRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateLibraryRepositoryMappingsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, request, user, ct)
            )
            .WithName("UpdateLibraryRepositoryMappings")
            .Produces<LibraryRepositoryMappingsResponse>()
            .Produces<LibraryError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Map repositories to a library.")
            .WithDescription(
                """
                Replaces the full set of repositories mapped to this library. Repositories whose ID
                appears in `repositoryIds` gain this library in their reviewer-tool context; all
                others lose it. Pass an empty array to clear all mappings.

                All IDs must belong to the caller's organization; an unknown or cross-organization
                ID returns 400.
                """
            )
            .RequireActiveOrganization();
    }
}
