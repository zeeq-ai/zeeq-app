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
    /// Largest supported organization activation-key lifetime.
    /// </summary>
    public const int MaxSupportedOrganizationActivationKeyLifetimeDays = 36_000;

    /// <summary>
    /// Allow-list of `provider:subject` identities granted the system-admin role.
    /// </summary>
    /// <remarks>
    /// Never match system-admin status on email. Email can be unverified or reused
    /// across providers; `provider:subject` is the stable, IdP-verified identity key.
    /// </remarks>
    public string[] SystemAdminSubjects { get; init; } = [];

    /// <summary>
    /// Requires activation keys for newly-created organizations when enabled.
    /// </summary>
    public bool OrganizationActivationKeysEnabled { get; init; }

    /// <summary>
    /// Default activation-key validity window in days.
    /// </summary>
    public int OrganizationActivationKeyDefaultLifetimeDays { get; init; } = 90;

    /// <summary>
    /// Maximum activation-key validity window in days.
    /// </summary>
    public int OrganizationActivationKeyMaxLifetimeDays { get; init; } = 365;

    /// <summary>
    /// Validates the cross-property activation-key lifetime bounds.
    /// </summary>
    public bool HasValidOrganizationActivationKeyLifetimeBounds() =>
        OrganizationActivationKeyDefaultLifetimeDays
            is >= 1
                and <= MaxSupportedOrganizationActivationKeyLifetimeDays
        && OrganizationActivationKeyMaxLifetimeDays
            is >= 1
                and <= MaxSupportedOrganizationActivationKeyLifetimeDays
        && OrganizationActivationKeyMaxLifetimeDays >= OrganizationActivationKeyDefaultLifetimeDays;
}
