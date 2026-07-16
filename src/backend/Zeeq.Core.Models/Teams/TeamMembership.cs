namespace Zeeq.Core.Models;

/// <summary>
/// Membership record linking a <see cref="User"/> to a <see cref="Team"/>
/// with a specific role.
/// </summary>
/// <remarks>
/// <para>
/// Every user has at least one team membership in their organization's root
/// team (created automatically on first login). The composite key is
/// <c>(OrganizationId, TeamId, UserId)</c>.
/// </para>
/// <para>
/// <see cref="OrganizationId"/> is denormalized for efficient
/// organization-scoped queries and tenant isolation. References to this
/// row should use the composite <c>(OrganizationId, TeamId)</c> key rather
/// than <see cref="TeamId"/> alone.
/// </para>
/// <para>Backed by the <c>core_team_memberships</c> table.</para>
/// </remarks>
public sealed class TeamMembership : ITeamScopedEntity, ICreatedEntity, ICanBeDisabled
{
    /// <summary>
    /// Organization the team belongs to (denormalized for tenant
    /// isolation).
    /// </summary>
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Team the user belongs to.
    /// </summary>
    public required string TeamId { get; init; }

    /// <summary>
    /// User who is a member of the team.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Role within the team (e.g. <c>"admin"</c>, <c>"member"</c>).
    /// </summary>
    public required string Role { get; set; }

    /// <summary>
    /// Local user who created this membership.
    /// </summary>
    public required string CreatedByUserId { get; init; }

    /// <summary>
    /// UTC timestamp when the team membership was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Set when the membership is disabled; <see langword="null"/> means
    /// active.
    /// </summary>
    public DateTimeOffset? DisabledAtUtc { get; set; }
}
