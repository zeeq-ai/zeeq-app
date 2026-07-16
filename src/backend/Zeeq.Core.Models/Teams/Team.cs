namespace Zeeq.Core.Models;

/// <summary>
/// Team inside an <see cref="Organization"/>, used to group users and
/// scope access to content partitions.
/// </summary>
/// <remarks>
/// <para>
/// Every organization has a root team (<see cref="IsRootTeam"/> =
/// <see langword="true"/>) created automatically with the organization.
/// The root team represents org-wide access and cannot be deleted.
/// Additional teams can be created for finer-grained grouping.
/// </para>
/// <para>
/// Rows that reference both <see cref="OrganizationId"/> and a team ID
/// should use the composite <c>(OrganizationId, Id)</c> key, not the team
/// ID alone, to ensure correct tenant isolation.
/// </para>
/// <para>Backed by the <c>core_teams</c> table.</para>
/// </remarks>
public sealed class Team : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <summary>
    /// Organization this team belongs to.
    /// </summary>
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Human-readable team name.
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// <see langword="true"/> for the automatically-created root team that
    /// represents org-wide access. Root teams cannot be deleted.
    /// </summary>
    public bool IsRootTeam { get; init; }

    /// <summary>
    /// Local user who created this team.
    /// </summary>
    public required string CreatedByUserId { get; init; }
}
