namespace Zeeq.Core.Models;

/// <summary>
/// Local Zeeq user resolved from one or more verified
/// <see cref="ExternalUserIdentity"/> records.
/// </summary>
/// <remarks>
/// <para>
/// The user is created on first external login. Subsequent logins from the
/// same external <c>(provider, provider_subject)</c> pair resolve to the
/// same user. The user's <see cref="EntityBase.Id"/> is the local <c>sub</c> carried
/// in cookies, OpenIddict access tokens, and API responses.
/// </para>
/// <para>
/// Disabled users must fail closed: if <see cref="DomainEntityBase.DisabledAtUtc"/> is set,
/// all authentication attempts must be rejected before any session state
/// or token is issued.
/// </para>
/// <para>Backed by the <c>core_users</c> table.</para>
/// </remarks>
public sealed class User : MutableDomainEntityBase
{
    /// <summary>
    /// Human-readable display name (synced from external IdP on login).
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// Primary email (synced from external IdP on login).
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Whether the email has been verified by the external IdP.
    /// </summary>
    public bool EmailVerified { get; set; }

    /// <summary>
    /// Photo/avatar URL from the external IdP (e.g. Google <c>picture</c>
    /// claim, GitHub <c>avatar_url</c>). Synced on each login.
    /// </summary>
    public string? PictureUrl { get; set; }

    /// <summary>
    /// Updated on each successful login for audit visibility.
    /// </summary>
    public DateTimeOffset? LastLoginAtUtc { get; set; }
}
