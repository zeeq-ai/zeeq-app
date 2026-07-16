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
/// GitHub App installation endpoints.
/// </summary>
public sealed class GitHubInstallationEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("integrations/github/install").WithTags("GitHub");

        // GET /api/v1/integrations/github/install/link
        group
            .MapGet(
                "/link",
                static (
                    ClaimsPrincipal user,
                    [FromServices] GitHubInstallationLinkHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(user, ct)
            )
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireAuthorization(a => a.RequireRole("owner", "admin"))
            .WithName("CreateGitHubInstallationLink")
            .WithSummary("Start GitHub App installation.")
            .WithDescription(
                """
                Issues a redirect to GitHub's App installation flow so the current
                organization can be connected to the Zeeq GitHub App. A signed `state`
                value ties the resulting callback back to this org.

                The caller must be authenticated and hold the `owner` or `admin` role in the
                active organization.
                """
            );

        // GET /api/v1/integrations/github/install/callback
        group
            .MapGet(
                "/callback",
                static (
                    [FromQuery(Name = "installation_id")] long? installationId,
                    [FromQuery(Name = "setup_action")] string? setupAction,
                    [FromQuery] string? state,
                    [FromServices] GitHubInstallationCallbackHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(installationId, setupAction, state, ct)
            )
            .AllowAnonymous()
            .WithName("CompleteGitHubInstallation")
            .WithSummary("Complete GitHub App installation.")
            .WithDescription(
                """
                Landing endpoint GitHub redirects the user back to after they approve (or
                cancel) the App installation. It validates the signed `state`, records the
                `installation_id` against the originating organization, and redirects the
                browser back into the app.

                Anonymous by design because GitHub drives the redirect; trust comes from the
                signed `state` rather than the session.
                """
            );
    }
}
