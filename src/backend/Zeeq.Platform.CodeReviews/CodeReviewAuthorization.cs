namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Resolves organization membership for code-review API handlers.
/// </summary>
/// <remarks>
/// The Phase 6 read endpoints expose organization-scoped PR and review state.
/// Non-members receive <c>404</c> so callers cannot distinguish an existing
/// organization from a missing one. Later admin endpoints can layer owner/admin
/// checks on top of this same access object.
/// </remarks>
public sealed class CodeReviewAuthorization(IZeeqMembershipStore memberships)
{
    /// <summary>
    /// Resolves the caller's active membership for the requested organization.
    /// </summary>
    public async Task<CodeReviewAccess?> ResolveAsync(
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

        return membership is null ? null : new(userId, membership.Role);
    }
}

/// <summary>
/// Caller access state for one organization.
/// </summary>
public sealed record CodeReviewAccess(string UserId, string Role)
{
    /// <summary>
    /// True when the caller can manage code-review configuration.
    /// </summary>
    public bool CanManage =>
        Role.Equals("owner", StringComparison.OrdinalIgnoreCase)
        || Role.Equals("admin", StringComparison.OrdinalIgnoreCase);
}
