using System.Security.Claims;
using Zeeq.Core.Identity;
using OpenIddict.Abstractions;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Builds the in-process, org-scoped identity reviewer-agent tool calls carry
/// on the GitHub webhook path, where no real end-user principal exists.
/// </summary>
/// <remarks>
/// This principal is never issued by OpenIddict and never serialized to a
/// token — it exists only to satisfy `ClaimsPrincipalExtensions.AsZeeqMinimalIdentity()`
/// (<c>Zeeq.Core.Identity/IdentityExtensions.cs</c>), which is all
/// <c>DocumentLibraryMcpTools</c> methods read off the caller.
/// </remarks>
public static class CodeReviewAutomationIdentity
{
    private const string AutomationSubject = "system:code-review-agent";

    /// <summary>
    /// Creates a synthetic <see cref="ClaimsPrincipal"/> scoped to the given
    /// organization and optional team.
    /// </summary>
    public static ClaimsPrincipal Create(string organizationId, string? teamId)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "CodeReviewAgent",
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role
        );

        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, AutomationSubject));
        identity.AddClaim(new Claim(AuthClaims.OrganizationId, organizationId));

        if (!string.IsNullOrWhiteSpace(teamId))
        {
            identity.AddClaim(new Claim(AuthClaims.TeamId, teamId));
        }

        return new ClaimsPrincipal(identity);
    }
}
