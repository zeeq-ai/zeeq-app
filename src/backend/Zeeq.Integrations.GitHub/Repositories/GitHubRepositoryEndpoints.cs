using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

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
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"));
        group.RequireRouteOrganizationMatchesCookie();

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
                Returns the GitHub repositories already registered for Zeeq code review in
                the route organization, along with their local settings. These mappings are
                what lets incoming webhooks resolve a repository to this org; an unmapped
                repository's PR and comment webhooks are acknowledged as a no-op.

                Requires the `owner` or `admin` role.
                """
            );

        // GET /api/v1/orgs/{orgId}/integrations/github/repositories/available
        group
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
                annotated with whether it is already configured in Zeeq. This powers the
                settings picker, which shows available and already-configured repositories in
                a single list.

                Requires a connected GitHub App installation and the `owner` or `admin` role.
                """
            );

        // POST /api/v1/orgs/{orgId}/integrations/github/repositories
        group
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
                of being dropped as a no-op.

                Requires the `owner` or `admin` role.
                """
            );

        // PUT /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}
        group
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
                identified by `repositoryId`. This changes how Zeeq handles the repository;
                it does not alter anything on GitHub.

                Requires the `owner` or `admin` role.
                """
            );

        // DELETE /api/v1/orgs/{orgId}/integrations/github/repositories/{repositoryId}
        group
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
