using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Browser-authenticated endpoints for testing/tuning hybrid snippet search within a library.
/// </summary>
/// <remarks>
/// Sibling group to <see cref="DocumentEndpoints"/>, not a standalone top-level route — org and
/// library both belong in the route (not a request body), exactly like the existing document
/// endpoints, and use the exact same auth/org-match setup.
/// </remarks>
public sealed class SnippetEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/libraries/{name}/snippets")
            .WithTags("Library Snippets")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();
        group.RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/libraries/{name}/snippets/search?kind=section&query=...&excludeDocumentPaths=...&maxResults=...
        group
            .MapGet(
                "/search",
                static (
                    string orgId,
                    string name,
                    [FromQuery] string kind,
                    [FromQuery] string query,
                    [FromQuery] string[]? excludeDocumentPaths,
                    [FromQuery] int? maxResults,
                    [FromServices] SearchSnippetsHandler handler,
                    CancellationToken ct
                ) =>
                    handler.HandleAsync(
                        orgId,
                        name,
                        kind,
                        query,
                        excludeDocumentPaths,
                        maxResults,
                        ct
                    )
            )
            .WithName("SearchLibrarySnippets")
            .Produces<SnippetSearchResultResponse[]>()
            .Produces<DocumentError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary(
                "Search section or code snippets in a library by semantic + full-text hybrid ranking."
            );
    }
}
