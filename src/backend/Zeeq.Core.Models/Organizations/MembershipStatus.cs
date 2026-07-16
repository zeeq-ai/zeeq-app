namespace Zeeq.Core.Models;

/// <summary>
/// Lifecycle status of an <see cref="OrganizationMembership"/> row.
/// </summary>
public enum MembershipStatus
{
    /// <summary>Accepted membership or invitation.</summary>
    Active,

    /// <summary>Invitation sent but not yet accepted.</summary>
    Pending,

    /// <summary>Invitation declined by the recipient.</summary>
    Declined,

    /// <summary>Membership disabled (removed or left).</summary>
    Disabled,
}
