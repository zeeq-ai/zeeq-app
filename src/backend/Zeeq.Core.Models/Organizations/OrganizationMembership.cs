namespace Zeeq.Core.Models;

/// <summary>
/// Membership record linking a <see cref="User"/> to an
/// <see cref="Organization"/> with a specific role.
/// </summary>
/// <remarks>
/// <para>
/// Every user has at least one organization membership (created
/// automatically on first login).
/// </para>
/// <para>
/// <see cref="Status"/> distinguishes active memberships from pending
/// invitations and declined invitations. Pending rows have
/// <see cref="UserId"/> = <see langword="null"/> and
/// <see cref="InvitedEmail"/> set. When accepted,
/// <see cref="UserId"/> is populated and <see cref="Status"/>
/// transitions to <c>"active"</c>.
/// </para>
/// <para>Backed by the <c>core_organization_memberships</c> table.</para>
/// </remarks>
public sealed class OrganizationMembership : DomainEntityBase, IOrganizationScopedEntity
{
    /// <summary>
    /// Organization the user belongs to.
    /// </summary>
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Local user ID. <see langword="null"/> for pending invitations
    /// that have not yet been accepted by a registered user.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Role within the organization (e.g. <c>"owner"</c>,
    /// <c>"admin"</c>, <c>"member"</c>).
    /// </summary>
    public required string Role { get; set; }

    /// <summary>
    /// Lifecycle status. Defaults to <see cref="MembershipStatus.Active"/>.
    /// </summary>
    public required MembershipStatus Status { get; set; } = MembershipStatus.Active;

    /// <summary>
    /// Email address the invitation was sent to.
    /// <see langword="null"/> for active memberships.
    /// </summary>
    public string? InvitedEmail { get; init; }

    /// <summary>
    /// Local user who created this membership or sent the invitation.
    /// </summary>
    public required string CreatedByUserId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, this is the user's default
    /// organization. At most one org per user should have this set.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Expiry for pending invitations.
    /// <see langword="null"/> for active memberships.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
