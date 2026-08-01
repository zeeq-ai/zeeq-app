namespace Zeeq.Core.Models;

/// <summary>
/// System-minted activation-key provenance for enabling one organization.
/// </summary>
public sealed class OrganizationActivationKey : MutableDomainEntityBase
{
    /// <summary>
    /// SHA-256 hash of the activation key presented to users.
    /// </summary>
    public required string KeyHash { get; init; }

    /// <summary>
    /// Optional note supplied by the system administrator who created the key.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// System-admin user that minted the key.
    /// </summary>
    public required string CreatedByUserId { get; init; }

    /// <summary>
    /// UTC timestamp after which the key cannot be used.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>
    /// UTC timestamp when the key was successfully exchanged.
    /// </summary>
    public DateTimeOffset? ActivatedAtUtc { get; set; }

    /// <summary>
    /// Organization activated by the exchange, once consumed.
    /// </summary>
    public string? ActivatedOrganizationId { get; set; }

    /// <summary>
    /// User who exchanged the key, once consumed.
    /// </summary>
    public string? ActivatedByUserId { get; set; }
}
