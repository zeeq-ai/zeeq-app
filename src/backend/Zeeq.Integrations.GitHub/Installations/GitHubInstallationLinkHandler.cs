using System.Security.Claims;
using System.Security.Cryptography;
using Zeeq.Core.Common;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Starts the GitHub App installation flow for the current organization.
/// </summary>
public sealed class GitHubInstallationLinkHandler(
    GitHubSettings settings,
    GitHubInstallationStateTokenProtector stateProtector
) : IEndpointHandler
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates a signed install state token and redirects to GitHub.
    /// </summary>
    /// <remarks>
    /// This is the first step in the GitHub App install flow. A signed-in Zeeq
    /// owner or admin calls this handler from the settings UI. The handler reads
    /// the current Zeeq user, organization, and team from the local auth
    /// claims, puts that data into <see cref="GitHubInstallationStatePayload"/>,
    /// protects it with <see cref="GitHubInstallationStateTokenProtector"/>, and
    /// sends the browser to GitHub.
    ///
    /// GitHub later sends the browser back to
    /// <see cref="GitHubInstallationCallbackHandler"/> with the protected state
    /// token and the GitHub installation id. The callback uses that state to
    /// link the installation to the same Zeeq organization that started here.
    /// </remarks>
    public IResult HandleAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.AppSlug))
        {
            return Results.BadRequest(
                new GitHubInstallationError("GitHub AppSlug is not configured.")
            );
        }

        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        var organizationId = user.FindFirstValue(AuthClaims.OrganizationId);

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        var payload = new GitHubInstallationStatePayload(
            OrganizationId: organizationId,
            TeamId: user.FindFirstValue(AuthClaims.TeamId),
            UserId: userId,
            Nonce: Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)),
            ExpiresAtUtc: DateTimeOffset.UtcNow.Add(StateLifetime)
        );

        var state = stateProtector.Protect(payload);

        var installUrl =
            $"https://github.com/apps/{Uri.EscapeDataString(settings.AppSlug)}/installations/new"
            + $"?state={Uri.EscapeDataString(state)}";

        return Results.Redirect(installUrl);
    }
}

/// <summary>
/// Error response returned by GitHub installation endpoints.
/// </summary>
public sealed record GitHubInstallationError(string Message);
