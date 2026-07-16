using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// System-admin endpoint for manually triggering a public repository source's
/// ingest.
/// </summary>
/// <remarks>
/// Mapped under <c>/api/v1/admin</c> with live system-admin authorization,
/// inherited from the admin route group — the runtime endpoint mapper routes
/// any <see cref="ISystemAdminEndpoint"/> into that group automatically.
/// Public sources are system-scoped, not organization-scoped (spec §2), so
/// this trigger has no tenant boundary to check — only that the caller is a
/// system admin.
/// </remarks>
public sealed class IngestAdminEndpoints : ISystemAdminEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("public-sources").WithTags("Ingest");

        // POST /api/v1/admin/public-sources/{publicSourceId}/ingest-run
        group
            .MapPost(
                "/{publicSourceId}/ingest-run",
                static (
                    [MaxLength(36)] string publicSourceId,
                    [FromServices] TriggerPublicSourceIngestHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(publicSourceId, ct)
            )
            .WithName("TriggerPublicSourceIngest")
            .Produces<TriggerIngestRunResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<IngestError>(StatusCodes.Status409Conflict)
            .Produces<IngestError>(StatusCodes.Status429TooManyRequests)
            .WithSummary("Manually trigger a public source's repository sync.")
            .WithDescription(
                """
                Queues an immediate sync of a public repository source, outside its normal
                schedule. System-admin only.

                Returns `404` when the source doesn't exist, `409` when a sync is already
                queued or running, and `429` when more than 5 manual triggers have been
                requested in the last hour.
                """
            );
    }
}
