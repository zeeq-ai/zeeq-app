namespace Zeeq.Core.Identity;

/// <summary>
/// Reserved system-level role values used by platform operator features.
/// </summary>
/// <remarks>
/// These values share the OpenIddict role claim type with organization roles for
/// compatibility, but system authorization is always decided by live
/// `provider:subject` configuration checks. Organization role sources must never
/// issue <see cref="SystemAdmin"/> as an organization membership role.
/// </remarks>
public static class SystemRoles
{
    /// <summary>
    /// Reserved role value for system administrators.
    /// </summary>
    public const string SystemAdmin = "system-admin";
}
