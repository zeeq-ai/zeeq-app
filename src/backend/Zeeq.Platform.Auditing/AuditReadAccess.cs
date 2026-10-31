using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Auditing;

/// <summary>Current read authorization for one caller in one organization.</summary>
/// <param name="OrganizationId">Organization taken from the caller's server-issued claims.</param>
/// <param name="TeamId">Caller's active team, used for team-scoped plus org-shared events.</param>
/// <param name="IsOrgAdmin">Owner or admin: may read org-wide, security, and deleted-subject history.</param>
/// <param name="LibraryIds">
/// For non-admins, the libraries the caller can currently access; <see langword="null"/> for admins.
/// </param>
public sealed record AuditReadScope(
    string OrganizationId,
    string? TeamId,
    bool IsOrgAdmin,
    IReadOnlyCollection<string>? LibraryIds
);

/// <summary>
/// Resolves read scope from live membership (not stale claims) and current library access.
/// </summary>
/// <remarks>
/// Owners and admins see all history of their own organization. Other members see only events of
/// libraries they can currently access, so org-wide membership/security events and history of
/// deleted libraries are not visible to them. Organization is never taken from request input.
/// Historical actor roles and retained snapshots do not preserve a removed reader's access.
/// This decision is shared by list and detail requests; hiding rows only in the UI is insufficient.
/// </remarks>
public class AuditReadAccess(IZeeqMembershipStore memberships, ILibraryDocumentStore libraries)
{
    /// <summary>Resolves the caller's current scope, or <see langword="null"/> when they have no active membership.</summary>
    public virtual async Task<AuditReadScope?> ResolveAsync(
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var identity = user.AsZeeqIdentity();

        if (
            string.IsNullOrWhiteSpace(identity.OrganizationId)
            || string.IsNullOrWhiteSpace(identity.OwnerUserId)
        )
        {
            return null;
        }

        var membership = (
            await memberships.ListActiveMembershipsForUserAsync(identity.OwnerUserId, ct)
        ).FirstOrDefault(m => m.OrganizationId == identity.OrganizationId);

        return await BuildScopeAsync(
            identity,
            membership?.Role,
            token => libraries.ListLibrariesAsync(identity.OrganizationId, token),
            ct
        );
    }

    /// <summary>
    /// Pure scope decision: no role means no access; owner/admin see everything in the organization;
    /// other members are limited to libraries they can currently access (org-shared, or their team's).
    /// Libraries are only loaded when needed.
    /// </summary>
    internal static async Task<AuditReadScope?> BuildScopeAsync(
        ZeeqIdentity identity,
        string? role,
        Func<CancellationToken, Task<IReadOnlyList<Library>>> loadLibraries,
        CancellationToken ct
    )
    {
        if (role is null)
        {
            return null;
        }

        if (role is "owner" or "admin")
        {
            return new AuditReadScope(identity.OrganizationId, null, true, null);
        }

        var accessible = (await loadLibraries(ct))
            .Where(library => library.TeamId is null || library.TeamId == identity.TeamId)
            .Select(library => library.Id)
            .ToArray();

        return new AuditReadScope(identity.OrganizationId, identity.TeamId, false, accessible);
    }
}
