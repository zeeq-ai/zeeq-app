using Microsoft.AspNetCore.Authorization;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Browser-authenticated endpoints for organization CRUD and slug
/// availability checks.
/// </summary>
public sealed partial class OrganizationEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = Core.Identity.SetupIdentityExtension.CookieScheme,
                }
            );

        // GET /api/v1/orgs
        group
            .MapGet(
                "/",
                static (
                    ClaimsPrincipal user,
                    [FromServices] GetOrganizationsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(user, ct)
            )
            .WithName("GetOrganizations")
            .WithTags("Organizations")
            .WithSummary("List my organizations.")
            .WithDescription(
                """
                Returns every organization the authenticated user is a member of, with the
                user's role in each. This is the set the user can switch between as their
                active organization.
                """
            );

        // POST /api/v1/orgs
        group
            .MapPost(
                "/",
                static (
                    CreateOrganizationRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateOrganizationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(request, user, ct)
            )
            .WithName("CreateOrganization")
            .WithTags("Organizations")
            .WithSummary("Create an organization.")
            .WithDescription(
                """
                Creates a new organization with the authenticated user as its first `owner`,
                provisioning the root team alongside it. The chosen slug must be available
                across all organizations.
                """
            );

        var orgGroup = group.MapGroup("/{orgId}").RequireRouteOrganizationMatchesCookie();

        // GET /api/v1/orgs/{orgId}
        orgGroup
            .MapGet(
                "/",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] GetOrganizationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("GetOrganization")
            .WithTags("Organizations")
            .WithSummary("Get an organization.")
            .WithDescription(
                """
                Returns the details (name, slug, icon, and related metadata) for the
                organization identified by `orgId`. The authenticated user must be a member.
                """
            );

        // PUT /api/v1/orgs/{orgId}
        orgGroup
            .MapPut(
                "/",
                static (
                    string orgId,
                    UpdateOrganizationRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateOrganizationHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .RequireActiveOrganization()
            .WithName("UpdateOrganization")
            .WithTags("Organizations")
            .WithSummary("Update an organization.")
            .WithDescription(
                """
                Updates the editable profile fields — name, slug, or icon — of the organization
                identified by `orgId`. A changed slug must still be available.

                Requires the `owner` or `admin` role in the organization.
                """
            );

        // PUT /api/v1/orgs/{orgId}/same-domain-onboarding
        orgGroup
            .MapPut(
                "/same-domain-onboarding",
                static (
                    string orgId,
                    UpdateSameDomainOnboardingRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateSameDomainOnboardingHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, request, user, ct)
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .RequireActiveOrganization()
            .WithName("UpdateSameDomainOnboarding")
            .WithTags("Organizations")
            .WithSummary("Update same-domain onboarding settings.")
            .WithDescription(
                """
                Enables or disables automatic same-domain invitations for the organization.
                Enabling derives the claimable domain from the organization creator's email.

                Requires the `owner` or `admin` role in the organization.
                """
            );

        // GET /api/v1/orgs/slug-check?slug=...&excludeOrgId=...
        group
            .MapGet(
                "/slug-check",
                static (
                    string slug,
                    string? excludeOrgId,
                    [FromServices] CheckSlugHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(slug, excludeOrgId, ct)
            )
            .WithName("CheckSlug")
            .WithTags("Organizations")
            .WithSummary("Check slug availability.")
            .WithDescription(
                """
                Reports whether the given `slug` is free to use for an organization. Pass
                `excludeOrgId` when editing an existing organization so its own current slug is
                not counted as a conflict.

                Intended for live validation while creating or renaming an organization.
                """
            );

        orgGroup.MapOrganizationMemberEndpoints();
    }
}
