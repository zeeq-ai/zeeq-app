namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Platform-wide operator and system configuration.
    /// </summary>
    public PlatformSettings Platform { get; init; } = new();
}

/// <summary>
/// Platform-wide settings for system operator capabilities.
/// </summary>
public sealed record PlatformSettings
{
    /// <summary>
    /// Allow-list of `provider:subject` identities granted the system-admin role.
    /// </summary>
    /// <remarks>
    /// Never match system-admin status on email. Email can be unverified or reused
    /// across providers; `provider:subject` is the stable, IdP-verified identity key.
    /// </remarks>
    public string[] SystemAdminSubjects { get; init; } = [];
}
