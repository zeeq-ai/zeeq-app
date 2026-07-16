namespace Zeeq.Core.Models;

/// <summary>
/// External identity provider (IdP) subject bound to a local
/// <see cref="User"/>.
/// </summary>
/// <remarks>
/// <para>
/// Human identity is owned by the external IdP (Google, Office 365, etc.),
/// not by OpenIddict. This row is the durable link between a verified
/// external <c>(provider, provider_subject)</c> pair and the local Zeeq
/// user.
/// </para>
/// <para>
/// A single <see cref="User"/> may have multiple external identities if
/// they authenticate through different providers that resolve to the same
/// local account. The converse is not true: each external
/// <c>(provider, provider_subject)</c> pair maps to exactly one local user.
/// </para>
/// <para>Backed by the <c>auth_user_identities</c> table.</para>
/// </remarks>
public sealed class ExternalUserIdentity
{
    /// <summary>
    /// Local <see cref="User"/> identifier this identity resolves to.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// External IdP name (e.g. <c>"google"</c>, <c>"office365"</c>).
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Subject identifier from the external IdP's ID token or userinfo
    /// response.
    /// </summary>
    public required string ProviderSubject { get; init; }

    /// <summary>
    /// Email from the external IdP (may be used for account linking).
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Whether the external IdP has verified the email.
    /// </summary>
    public bool EmailVerified { get; set; }

    /// <summary>
    /// Display name from the external IdP profile.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Photo/avatar URL from the external IdP. Per-provider; the
    /// <see cref="User.PictureUrl"/> is synced from the most recent login.
    /// </summary>
    public string? PictureUrl { get; set; }

    /// <summary>
    /// Timestamp of the first successful authentication with this identity.
    /// </summary>
    public DateTimeOffset FirstSeenAtUtc { get; init; }

    /// <summary>
    /// Timestamp of the most recent successful authentication; updated on
    /// each login.
    /// </summary>
    public DateTimeOffset LastSeenAtUtc { get; set; }

    /// <summary>
    /// Set when this identity is disabled; must block login before any
    /// session state is updated (fail closed).
    /// </summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }
}
