using System.Text.Json.Serialization;

namespace Zeeq.Core.Models;

/// <summary>
/// Organization-scoped alternate identity value for a local Zeeq user.
/// </summary>
/// <remarks>
/// Aliases are matching keys, not profile preferences. They let org-local
/// telemetry and provider data, such as agent owner emails or GitHub logins,
/// resolve to the correct Zeeq member without rewriting historical source rows.
/// </remarks>
public sealed class UserAlias : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Local user that owns this alias in the organization.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Identity namespace for the alias value.
    /// </summary>
    public required UserAliasKind Kind { get; init; }

    /// <summary>
    /// User-entered display value, preserving readable casing where possible.
    /// </summary>
    public required string DisplayValue { get; set; }

    /// <summary>
    /// Canonical lookup value used by joins and uniqueness checks.
    /// </summary>
    public required string NormalizedValue { get; init; }

    /// <summary>
    /// Time the alias was verified, once verification flows exist.
    /// </summary>
    public DateTimeOffset? VerifiedAtUtc { get; set; }
}

/// <summary>
/// Supported user alias namespaces.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserAliasKind>))]
public enum UserAliasKind
{
    /// <summary>
    /// Email address alias, normalized case-insensitively.
    /// </summary>
    [JsonStringEnumMemberName("email")]
    Email,

    /// <summary>
    /// GitHub login alias, normalized case-insensitively without a leading at sign.
    /// </summary>
    [JsonStringEnumMemberName("github")]
    GitHub,
}
