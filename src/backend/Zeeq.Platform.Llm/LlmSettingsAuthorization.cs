namespace Zeeq.Platform.Llm;

/// <summary>
/// Resolves organization membership and management access for LLM settings handlers.
/// </summary>
/// <remarks>
/// Members may open the route, but only owners and admins can view settings,
/// manage keys, or test provider access. Non-members are hidden with NotFound.
/// </remarks>
public sealed class LlmSettingsAuthorization(IZeeqMembershipStore memberships)
{
    /// <summary>
    /// Resolves the caller's active membership for the organization.
    /// </summary>
    public async Task<LlmSettingsAccess?> ResolveAsync(
        string organizationId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var membership = (
            await memberships.ListActiveMembershipsForUserAsync(userId, cancellationToken)
        ).FirstOrDefault(row => row.OrganizationId == organizationId);

        return membership is null ? null : new LlmSettingsAccess(userId, membership.Role);
    }
}

/// <summary>
/// Caller access state for one organization.
/// </summary>
public sealed record LlmSettingsAccess(string UserId, string Role)
{
    /// <summary>
    /// True when the caller can manage LLM settings and encrypted keys.
    /// </summary>
    public bool CanManage =>
        Role.Equals("owner", StringComparison.OrdinalIgnoreCase)
        || Role.Equals("admin", StringComparison.OrdinalIgnoreCase);
}
