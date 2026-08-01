using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// System-admin activation-key management endpoints.
/// </summary>
/// <remarks>
/// These routes manage unassigned organization activation keys. Keys are
/// provenance that a system administrator minted an activation right; they are
/// not assigned to an organization until a user exchanges one for their own
/// inactive organization.
///
/// The create route returns raw key material exactly once. After that boundary,
/// the backend stores and compares only hashes, so system admins must copy the
/// key from the creation response before closing the client-side reveal panel.
/// </remarks>
public sealed class SystemActivationKeyEndpoints : ISystemAdminEndpoint
{
    /// <summary>
    /// Registers system-admin activation-key routes.
    /// </summary>
    /// <remarks>
    /// These endpoints are mounted under the system-admin route group, so route
    /// handlers can focus on key lifecycle behavior instead of repeating
    /// system-admin authorization wiring. Activated keys remain immutable audit
    /// records; revocation applies only to keys that have never been activated.
    /// </remarks>
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        // System-admin routes use a dedicated tag so generated clients and the
        // OpenAPI UI keep key management separate from user-facing activation.
        var group = app.MapGroup("activation-keys").WithTags("SystemActivationKeys");

        group
            .MapGet(
                "/",
                static (
                    [Range(1, 10_000)] int page,
                    [Range(1, 100)] int pageSize,
                    [MaxLength(200)] string? q,
                    OrganizationActivationKeyStatus? status,
                    [FromServices] ListSystemActivationKeysHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(page, pageSize, q, status, ct)
            )
            .WithName("ListSystemActivationKeys")
            .WithSummary("List activation keys.")
            .WithDescription(
                """
                Lists activation keys for system administration, including status,
                creator, expiry, and activation metadata.

                Requires system-admin permissions. The result is paged and can be
                filtered by status or searched by key metadata such as note, creator,
                and activated organization id.
                """
            );

        group
            .MapPost(
                "/",
                static (
                    CreateSystemActivationKeyRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateSystemActivationKeyHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(request, user, ct)
            )
            .WithName("CreateSystemActivationKey")
            .WithSummary("Create activation key.")
            .WithDescription(
                """
                Creates one unassigned organization activation key and returns the raw
                key material exactly once. The backend stores only the key hash.

                Requires system-admin permissions. The key is later exchanged by an
                authenticated user with an inactive personal organization.
                """
            );

        group
            .MapDelete(
                "/{keyId}",
                static (
                    [MaxLength(128)] string keyId,
                    ClaimsPrincipal user,
                    [FromServices] RevokeSystemActivationKeyHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(keyId, user, ct)
            )
            // Revocation is intentionally scoped to never-activated keys so an exchanged
            // key remains an audit record for the organization activation event.
            .WithName("RevokeSystemActivationKey")
            .WithSummary("Revoke activation key.")
            .WithDescription(
                """
                Revokes one never-activated activation key so it can no longer be
                exchanged for organization activation.

                Requires system-admin permissions. Activated keys are immutable audit
                records and cannot be revoked through this route.
                """
            );
    }
}
