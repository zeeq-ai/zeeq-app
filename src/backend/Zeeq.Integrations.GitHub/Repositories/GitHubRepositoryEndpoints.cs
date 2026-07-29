using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// GitHub repository management endpoints for the current organization.
/// </summary>
/// <remarks>
/// These endpoints are the operator-controlled registration mechanism that lets
/// webhook ingress resolve GitHub repositories into Zeeq organizations. Without
/// a configured repository mapping, incoming PR/comment webhooks are acknowledged
/// as no-op before they can publish queue work.
/// </remarks>
public sealed class GitHubRepositoryEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("orgs/{orgId}/integrations/github/repositories")
            .WithTags("GitHub")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            );
        group.RequireRouteOrganizationMatchesCookie();

        var managementGroup = group
            .MapGroup("")
            .RequireAuthorization(a => a.RequireRole("owner", "admin"));

        // This route does not require the `owner` or `admin` role because it is used to populate the
        // "Add repository" UI, which is available to all members of the organization
        // GET /api/v1/orgs/{orgId}/integrations/github/repositories/configured
        group
            .MapGet(
                "/configured",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListConfiguredGitHubRepositoriesHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(user, ct)
            )
            .WithName("ListConfiguredGitHubRepositories")
            .WithSummary("List configured repositories.")
            .WithDescription(
                """
                Returns the GitHub repositories already registered in Zeeq for the route
                organization, along with their local settings. `Enabled` controls whether
                incoming PR and comment webhooks create code-review work. Library-source
                visibility is tracked separately and does not change GitHub App access.

                Requires active membership in the route organization.
                """
            );

        // NOTE: The routes in this block deliberately sit on `group`, not `managementGroup`. Library
        // mapping and prompt customization are ordinary repository configuration that any member of
        // the organization should be able to edit, whereas registering, pausing, and removing a
        // repository stay restricted to owners and admins. The request shapes here cannot express a
        // privileged change, which is what makes the looser authorization safe.

        // PUT /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}/libraries
        group
            .MapPut(
                "/{repositoryId}/libraries",
                static (
                    string orgId,
                    string repositoryId,
                    UpdateRepositoryLibrariesRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateRepositoryLibrariesHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(repositoryId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("UpdateRepositoryLibraries")
            .Produces<GitHubConfiguredRepositoryResponse>()
            .Produces<GitHubRepositoryManagementError>(StatusCodes.Status400BadRequest)
            .Produces<GitHubRepositoryManagementError>(StatusCodes.Status404NotFound)
            .WithSummary("Update repository library mapping.")
            .WithDescription(
                """
                Replaces the set of libraries reviewer agents may query for this repository. Every
                other repository setting is left untouched, so this is available to any member of the
                organization rather than only owners and admins.

                Requires active membership in the route organization.
                """
            );

        // GET /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}/prompts
        group
            .MapGet(
                "/{repositoryId}/prompts",
                static (
                    string orgId,
                    string repositoryId,
                    ClaimsPrincipal user,
                    [FromServices] ListRepositoryPromptsHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(repositoryId, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("ListRepositoryPrompts")
            .Produces<RepositoryPromptSummaryResponse[]>()
            .Produces<GitHubRepositoryManagementError>(StatusCodes.Status404NotFound)
            .WithSummary("List organization prompts for a repository.")
            .WithDescription(
                """
                Returns every organization-scoped MCP prompt alongside whether this repository has
                activated it and how many placeholder values it has saved. Prompt bodies and their
                declared placeholders are not included; fetch a single prompt for those.

                Requires active membership in the route organization.
                """
            );

        // GET /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}/prompts/{documentId}
        group
            .MapGet(
                "/{repositoryId}/prompts/{documentId}",
                static (
                    string orgId,
                    string repositoryId,
                    string documentId,
                    [FromQuery] string libraryId,
                    ClaimsPrincipal user,
                    [FromServices] GetRepositoryPromptHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(repositoryId, documentId, libraryId, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("GetRepositoryPrompt")
            .Produces<RepositoryPromptDetailResponse>()
            .Produces<GitHubRepositoryManagementError>(StatusCodes.Status400BadRequest)
            .Produces<GitHubRepositoryManagementError>(StatusCodes.Status404NotFound)
            .WithSummary("Get a prompt's placeholders for a repository.")
            .WithDescription(
                """
                Returns the placeholders declared by one organization prompt, each paired with this
                repository's saved value when it has one. Placeholders are parsed from the live
                prompt body, so edits to the prompt are reflected immediately.

                Requires active membership in the route organization.
                """
            );

        // PUT /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}/prompts/{documentId}
        group
            .MapPut(
                "/{repositoryId}/prompts/{documentId}",
                static (
                    string orgId,
                    string repositoryId,
                    string documentId,
                    SaveRepositoryPromptRequest request,
                    ClaimsPrincipal user,
                    [FromServices] SaveRepositoryPromptHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(repositoryId, documentId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("SaveRepositoryPrompt")
            .Produces<RepositoryPromptDetailResponse>()
            .Produces<GitHubRepositoryManagementError>(StatusCodes.Status400BadRequest)
            .Produces<GitHubRepositoryManagementError>(StatusCodes.Status404NotFound)
            .WithSummary("Save a repository's prompt customization.")
            .WithDescription(
                """
                Activates or deactivates one organization prompt for this repository and replaces its
                placeholder values. Activation gates substitution: when inactive, agents retrieving
                the prompt receive its authored defaults even though the saved values are retained.

                Requires active membership in the route organization.
                """
            );

        // GET /api/v1/orgs/{orgId}/integrations/github/repositories/available
        managementGroup
            .MapGet(
                "/available",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] ListAvailableGitHubRepositoriesHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(user, ct)
            )
            .WithName("ListAvailableGitHubRepositories")
            .WithSummary("List installable repositories.")
            .WithDescription(
                """
                Returns every repository the linked GitHub App installation can see, each
                annotated with whether it already has a Zeeq repository row and whether it
                should appear as a private library source. This list reflects GitHub App
                installation access; it is broader than webhook-enabled repositories.

                Requires a connected GitHub App installation and the `owner` or `admin` role.
                """
            );

        // POST /api/v1/orgs/{orgId}/integrations/github/repositories
        managementGroup
            .MapPost(
                "/",
                static (
                    string orgId,
                    GitHubCreateRepositoryMappingRequest request,
                    ClaimsPrincipal user,
                    [FromServices] CreateGitHubRepositoryMappingHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("CreateGitHubRepositoryMapping")
            .WithSummary("Register a repository.")
            .WithDescription(
                """
                Maps an installation-visible GitHub repository into the route organization so
                its pull-request and comment webhooks are routed to Zeeq code review instead
                of being dropped as a no-op when `Enabled` is true. This does not install the
                GitHub App or change GitHub-side repository access.

                Requires the `owner` or `admin` role.
                """
            );

        // PUT /api/v1/orgs/{orgId}/integrations/github/repositories/visibility
        managementGroup
            .MapPut(
                "/visibility",
                static (
                    string orgId,
                    GitHubUpdateRepositoryVisibilityRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateGitHubRepositoryVisibilityHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("UpdateGitHubRepositoryVisibility")
            .WithSummary("Update repository library visibility.")
            .WithDescription(
                """
                Updates whether an installation-visible GitHub repository appears as a
                private source option when creating a library. This is independent from
                `Enabled`: hiding a repository does not pause webhook-triggered code-review
                work, and showing a repository does not enable webhook processing.

                Requires the `owner` or `admin` role.
                """
            );

        // PUT /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}
        managementGroup
            .MapPut(
                "/{repositoryId}",
                static (
                    string orgId,
                    string repositoryId,
                    GitHubUpdateRepositoryMappingRequest request,
                    ClaimsPrincipal user,
                    [FromServices] UpdateGitHubRepositoryMappingHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(repositoryId, request, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("UpdateGitHubRepositoryMapping")
            .WithSummary("Update repository settings.")
            .WithDescription(
                """
                Updates the Zeeq-local settings for an already-configured repository mapping,
                identified by `repositoryId`. `Enabled` controls webhook-triggered
                code-review work; it does not control GitHub App installation access or
                library-source visibility.

                Requires the `owner` or `admin` role.
                """
            );

        // DELETE /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}
        managementGroup
            .MapDelete(
                "/{repositoryId}",
                static (
                    string orgId,
                    string repositoryId,
                    ClaimsPrincipal user,
                    [FromServices] DisableGitHubRepositoryMappingHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(repositoryId, user, ct)
            )
            .RequireActiveOrganization()
            .WithName("DisableGitHubRepositoryMapping")
            .WithSummary("Disable a repository mapping.")
            .WithDescription(
                """
                Disables the repository mapping identified by `repositoryId`, so its webhooks
                are once again ignored by Zeeq. The GitHub App installation is left in place;
                only the Zeeq-side routing is turned off.

                Requires the `owner` or `admin` role.
                """
            );
    }
}
