using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Browser-authenticated endpoints for a library's repository ingest —
/// manual trigger and run history.
/// </summary>
/// <remarks>
/// The admin-scoped counterpart for triggering a public source directly
/// (<c>POST /api/v1/admin/public-sources/{publicSourceId}/ingest-run</c>) is
/// <see cref="IngestAdminEndpoints"/> — separate types because
/// <see cref="Zeeq.Core.Identity.ISystemAdminEndpoint"/> routes to a
/// differently-authorized group (see spec §7). This group's trigger endpoint
/// also handles public-source-backed libraries (org-scoped, not admin-scoped
/// — see <see cref="TriggerLibraryIngestHandler"/>'s remarks).
/// </remarks>
public sealed class IngestEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/libraries")
            .WithTags("Ingest")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();

        // POST /api/v1/orgs/{orgId}/libraries/{name}/ingest-run
        group
            .MapPost(
                "/{name}/ingest-run",
                static (
                    [MaxLength(36)] string orgId,
                    [MaxLength(200)] string name,
                    ClaimsPrincipal user,
                    [FromServices] TriggerLibraryIngestHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, user, ct)
            )
            .WithName("TriggerLibraryIngest")
            .Produces<TriggerIngestRunResponse>()
            .Produces<IngestError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<IngestError>(StatusCodes.Status409Conflict)
            .Produces<IngestError>(StatusCodes.Status429TooManyRequests)
            .WithSummary("Manually trigger a library's repository sync.")
            .WithDescription(
                """
                Queues an immediate sync of the private repository mapped to this library,
                outside its normal schedule. Requires the library to be configured with a
                private repository source (`sourceKind`/`sourceRepoUrl` set).

                Returns `404` when the library doesn't exist, `400` when the library has no
                linked repository, `409` when a sync is already queued or running for this
                library, and `429` when more than 5 manual triggers have been requested in
                the last hour.
                """
            )
            .RequireActiveOrganization();

        // POST /api/v1/orgs/{orgId}/libraries/{name}/ingest-run/reset
        group
            .MapPost(
                "/{name}/ingest-run/reset",
                static (
                    [MaxLength(36)] string orgId,
                    [MaxLength(200)] string name,
                    ClaimsPrincipal user,
                    [FromServices] ResetLibraryIngestRunStateHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, user, ct)
            )
            .WithName("ResetLibraryIngestRunState")
            .Produces<ResetLibraryIngestRunStateResponse>()
            .Produces<IngestError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("Clear a stuck private library repository sync.")
            .WithDescription(
                """
                Clears a private repository library's queued/running sync state and makes it
                eligible to sync again immediately. If the active run row exists and is still
                running, it is marked `Stalled`. Public-source-backed libraries are not reset
                through this organization-scoped endpoint.
                """
            )
            .RequireActiveOrganization();

        // GET /api/v1/orgs/{orgId}/libraries/{name}/ingest-runs
        group
            .MapGet(
                "/{name}/ingest-runs",
                static (
                    [MaxLength(36)] string orgId,
                    [MaxLength(200)] string name,
                    string? cursor,
                    int? limit,
                    ClaimsPrincipal user,
                    [FromServices] ListLibraryIngestRunsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, name, cursor, limit, user, ct)
            )
            .WithName("ListLibraryIngestRuns")
            .Produces<IngestRunPageResponse>()
            .Produces<IngestError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("List a library's ingest run history.")
            .WithDescription(
                """
                Returns ingest run history for a repository-sourced library, newest first,
                cursor-paginated (`limit` defaults to 10, capped at 50). Pass `nextCursor`
                from the previous page's response as `cursor` to fetch the next page; omit
                `cursor` for the first page. For a public-source library, this is the
                source's shared history — every subscribing library's run history is the
                same rows.

                Returns `404` when the library doesn't exist, `400` when the library has no
                repository source (nothing to list) or `cursor` is malformed.
                """
            )
            .RequireActiveOrganization();
    }
}
