using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Narrow membership-activation projection used by the token-validation
/// membership check. Mirrors <see cref="OrganizationActivationState"/> but
/// scoped to a single user's membership rather than the whole organization.
/// </summary>
/// <param name="OrganizationId">Organization the membership belongs to.</param>
/// <param name="UserId">Local user ID the membership belongs to.</param>
/// <param name="Status">Lifecycle status of the membership row.</param>
/// <param name="DisabledAtIsSet">Whether the membership row has been disabled.</param>
public sealed record MembershipActivationState(
    string OrganizationId,
    string UserId,
    MembershipStatus Status,
    bool DisabledAtIsSet
)
{
    /// <summary>
    /// Whether the membership currently grants access to the organization.
    /// </summary>
    public bool IsActive => Status == MembershipStatus.Active && !DisabledAtIsSet;
}
