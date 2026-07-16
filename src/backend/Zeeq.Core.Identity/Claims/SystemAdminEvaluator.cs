using System.Security.Claims;

namespace Zeeq.Core.Identity;

/// <summary>
/// Evaluates system-admin status from the current request principal and live configuration.
/// </summary>
/// <remarks>
/// This is the authoritative source for admin authorization and `/me`. It reads
/// the current `Platform:SystemAdminSubjects` allow-list on every request so
/// grants and revocations take effect without requiring the user to sign in again.
/// </remarks>
public sealed class SystemAdminEvaluator(AppSettings appSettings)
{
    /// <summary>
    /// Returns whether the authenticated principal currently has system-admin access.
    /// </summary>
    public bool IsSystemAdmin(ClaimsPrincipal user) =>
        SystemAdminSubjectMatcher.IsSystemAdminSubject(
            user.FindFirstValue(AuthClaims.Provider),
            user.FindFirstValue(AuthClaims.ProviderSubject),
            appSettings.Platform.SystemAdminSubjects
        );
}
