using Zeeq.Core.Models;

namespace Zeeq.Testing.Biomes;

/// <summary>Mutable construction input for PendingInvitation test output.</summary>
public sealed class PendingInvitationInput
{
    /// <summary>Whether the invitation is a same-domain auto invite.</summary>
    public bool IsSameDomainAutoInvite { get; set; }

    /// <summary>
    /// Invited email address.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Membership role granted when the invitation is accepted.
    /// </summary>
    public string Role { get; set; } = "member";

    /// <summary>
    /// Created timestamp for the generated invitation.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Expiration timestamp for the generated invitation.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Disabled timestamp for the generated invitation.
    /// </summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }
}

/// <summary>Mutable construction input for Organization test output.</summary>
public sealed class OrganizationInput
{
    /// <summary>
    /// Organization display name.
    /// </summary>
    public string DisplayName { get; set; } = "Test Organization";

    /// <summary>
    /// Organization slug. A generated slug is used when omitted.
    /// </summary>
    public string? Slug { get; set; }

    /// <summary>
    /// User that owns and belongs to the generated organization.
    /// </summary>
    public User? Owner { get; set; }

    /// <summary>
    /// Role assigned to the owner membership.
    /// </summary>
    public string Role { get; set; } = "owner";

    /// <summary>
    /// Whether the generated owner membership is the user's default organization.
    /// </summary>
    public bool IsDefaultMembership { get; set; }

    /// <summary>
    /// Created timestamp for generated rows.
    /// </summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>
    /// Activation timestamp for the generated organization.
    /// </summary>
    public DateTimeOffset? ActivatedAtUtc { get; set; }

    /// <summary>
    /// Whether the generated organization should be active.
    /// </summary>
    public bool IsActivated { get; set; } = true;
}

/// <summary>Construction values for users added without memberships.</summary>
public sealed class UserInput
{
    /// <summary>Optional ID, otherwise generated.</summary>
    public string? Id { get; set; }

    /// <summary>Optional display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional email.</summary>
    public string? Email { get; set; }

    /// <summary>Whether the email is verified.</summary>
    public bool EmailVerified { get; set; } = true;

    /// <summary>Optional creation time.</summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>Optional update time.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
