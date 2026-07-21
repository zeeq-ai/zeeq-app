using Microsoft.AspNetCore.Authorization;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Browser-authenticated endpoints for managing markdown documents inside libraries.
/// </summary>
public sealed class DocumentEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/libraries/{name}/documents")
            .WithTags("Library Documents")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();
        group.RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/libraries/{name}/documents
        group
            .MapGet(
                "/",
                static (
                    string orgId,
                    string name,
                    [FromServices] ListDocumentsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, ct)
            )
            .WithName("ListLibraryDocuments")
            .Produces<DocumentResponse[]>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List documents.")
            .WithDescription(
                """
                Returns the document metadata rows for the route library.
                """
            );

        // PUT /api/v1/orgs/{orgId}/libraries/{name}/documents
        group
            .MapPut(
                "/",
                static (
                    string orgId,
                    string name,
                    UpsertDocumentRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpsertDocumentHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, request, user, ct)
            )
            .WithName("UpsertLibraryDocument")
            .Produces<DocumentResponse>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Upsert a document.")
            .WithDescription(
                """
                Creates or replaces a markdown document in the route library.
                """
            );

        // DELETE /api/v1/orgs/{orgId}/libraries/{name}/documents?path=...
        group
            .MapDelete(
                "/",
                static (
                    string orgId,
                    string name,
                    [FromQuery] string path,
                    [FromServices] DeleteDocumentHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, path, ct)
            )
            .WithName("DeleteLibraryDocument")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Delete a document.")
            .WithDescription(
                """
                Deletes one document from the route library by path.
                """
            );

        // GET /api/v1/orgs/{orgId}/libraries/{name}/documents/content?path=...
        group
            .MapGet(
                "/content",
                static (
                    string orgId,
                    string name,
                    [FromQuery] string path,
                    [FromServices] GetDocumentContentHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, path, ct)
            )
            .WithName("GetLibraryDocumentContent")
            .Produces<DocumentContentResponse>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Get document content.")
            .WithDescription(
                """
                Returns the full markdown body for one document by path.
                """
            );

        // GET /api/v1/orgs/{orgId}/libraries/{name}/documents/parse-preview?path=...
        group
            .MapGet(
                "/parse-preview",
                static (
                    string orgId,
                    string name,
                    [FromQuery] string path,
                    [FromServices] PreviewDocumentParseHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, path, ct)
            )
            .WithName("PreviewLibraryDocumentParse")
            .Produces<DocumentParsePreviewResponse>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Preview document parsing.")
            .WithDescription(
                """
                Shows the title, keywords, headings, and snippets the indexing pipeline
                would extract from a document without persisting changes.
                """
            );

        // GET /api/v1/orgs/{orgId}/libraries/{name}/documents/search?query=...&limit=...
        group
            .MapGet(
                "/search",
                static (
                    string orgId,
                    string name,
                    [FromQuery] string query,
                    [FromQuery] int? limit,
                    [FromServices] SearchDocumentsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, query, limit, ct)
            )
            .WithName("SearchLibraryDocuments")
            .Produces<DocumentSearchResultResponse[]>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Search documents.")
            .WithDescription(
                """
                Searches documents in the route library by keyword query.
                """
            );

        // POST /api/v1/orgs/{orgId}/libraries/{name}/documents/review-exclusion
        group
            .MapPost(
                "/review-exclusion",
                static (
                    string orgId,
                    string name,
                    SetDocumentReviewExclusionRequest request,
                    [FromServices] SetDocumentReviewExclusionHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, request, ct)
            )
            .WithName("SetLibraryDocumentReviewExclusion")
            .Produces<DocumentResponse>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Set review exclusion.")
            .WithDescription(
                """
                Sets or clears a document's code-review exclusion. Excluded documents do
                not surface to code-review agents via list/search tools, but direct reads
                by path still resolve. Synced documents are rejected.
                """
            );

        // POST /api/v1/orgs/{orgId}/libraries/{name}/documents/rename  (D-3)
        group
            .MapPost(
                "/rename",
                static (
                    string orgId,
                    string name,
                    RenameDocumentRequest request,
                    [FromServices] RenameDocumentHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, request, ct)
            )
            .WithName("RenameLibraryDocument")
            .Produces<DocumentResponse>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<DocumentError>(StatusCodes.Status409Conflict)
            .WithSummary("Rename a document.")
            .WithDescription(
                """
                Moves one document to a new path within the route library.
                """
            );
    }
}
