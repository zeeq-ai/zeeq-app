namespace Zeeq.Core.Identity;

/// <summary>
/// Local auth context produced after an external IdP identity is resolved.
/// </summary>
/// <remarks>
/// The local user ID is the OpenIddict subject for browser sessions and
/// user-delegated access tokens. Organization and team IDs provide the default
/// tenant context that downstream MCP/API authorization evaluates server-side.
/// </remarks>
/// <param name="UserId">Local app user ID.</param>
/// <param name="OrganizationId">Default organization for the user session.</param>
/// <param name="TeamId">Default team inside the organization.</param>
public sealed record AuthContext(string UserId, string OrganizationId, string TeamId);

/// <summary>
/// Resolves external IdP identities into local auth users and active org/team context.
/// </summary>
public sealed class AuthUserStore(IZeeqIdentityStore identityStore)
{
    /// <summary>
    /// Resolves or provisions the local app user for a verified upstream IdP identity.
    /// </summary>
    /// <remarks>
    /// External providers own human authentication. This method converts the verified
    /// provider subject into app-owned user/org/team records that cookies and
    /// OpenIddict access tokens can reference consistently.
    /// </remarks>
    public Task<AuthContext> EnsureUserAsync(
        string provider,
        string providerSubject,
        string? displayName,
        string? email,
        string? pictureUrl,
        CancellationToken cancellationToken
    ) =>
        identityStore.EnsureUserAsync(
            provider,
            providerSubject,
            displayName,
            email,
            pictureUrl,
            cancellationToken
        );
}
