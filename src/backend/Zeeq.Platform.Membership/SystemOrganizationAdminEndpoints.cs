using System.ComponentModel.DataAnnotations;

namespace Zeeq.Platform.Membership;

/// <summary>
/// System-admin endpoints for platform-wide organization management.
/// </summary>
public sealed class SystemOrganizationAdminEndpoints : ISystemAdminEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("organizations").WithTags("SystemOrganizations");

        // GET /api/v1/admin/organizations
        group
            .MapGet(
                "/",
                static (
                    [Range(1, 10_000)] int page,
                    [Range(1, 100)] int pageSize,
                    [MaxLength(200)] string? q,
                    [FromServices] ListSystemOrganizationsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(page, pageSize, q, ct)
            )
            .WithName("ListSystemOrganizations")
            .WithSummary("List organizations for system administrators.");

        // GET /api/v1/admin/organizations/{orgId}
        group
            .MapGet(
                "/{orgId}",
                static (
                    [MaxLength(128)] string orgId,
                    [FromServices] GetSystemOrganizationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, ct)
            )
            .WithName("GetSystemOrganization")
            .WithSummary("Get organization details for system administrators.");

        // GET /api/v1/admin/organizations/{orgId}/members
        group
            .MapGet(
                "/{orgId}/members",
                static (
                    [MaxLength(128)] string orgId,
                    [Range(1, 10_000)] int page,
                    [Range(1, 100)] int pageSize,
                    [FromServices] ListSystemOrganizationMembersHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, page, pageSize, ct)
            )
            .WithName("ListSystemOrganizationMembers")
            .WithSummary("List active organization members for system administrators.");

        // PATCH /api/v1/admin/organizations/{orgId}
        group
            .MapPatch(
                "/{orgId}",
                static (
                    [MaxLength(128)] string orgId,
                    UpdateSystemOrganizationRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateSystemOrganizationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .WithName("UpdateSystemOrganization")
            .WithSummary("Update organization activation or tier for system administrators.");
    }
}
