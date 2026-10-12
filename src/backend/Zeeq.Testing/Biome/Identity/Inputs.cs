using Zeeq.Core.Models;

namespace Zeeq.Testing.Biomes;

/// <summary>Mutable construction input for UserAlias test output.</summary>
public sealed class UserAliasInput
{
    /// <summary>
    /// Organization that scopes the alias. Defaults to the seed organization.
    /// </summary>
    public Organization? Organization { get; set; }

    /// <summary>
    /// Alias namespace.
    /// </summary>
    public UserAliasKind Kind { get; set; } = UserAliasKind.Email;

    /// <summary>
    /// User-facing alias value. A generated value is used when omitted.
    /// </summary>
    public string? DisplayValue { get; set; }

    /// <summary>
    /// Canonical lookup value. Defaults to a simple lower-case normalization of
    /// <see cref="DisplayValue" />.
    /// </summary>
    public string? NormalizedValue { get; set; }

    /// <summary>
    /// Timestamp when this alias was verified, if any.
    /// </summary>
    public DateTimeOffset? VerifiedAtUtc { get; set; }

    /// <summary>
    /// Created timestamp for the generated alias row.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Disabled timestamp for the generated alias row.
    /// </summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }
}
