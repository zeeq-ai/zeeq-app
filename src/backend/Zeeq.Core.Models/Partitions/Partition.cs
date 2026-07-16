namespace Zeeq.Core.Models;

/// <summary>
/// Selectable content partition scoped to an <see cref="Organization"/>
/// or <see cref="Team"/>.
/// </summary>
/// <remarks>
/// <para>
/// Partitions represent content subsets that users can select when creating
/// credentials or tokens. The <see cref="ScopeType"/> determines the parent
/// boundary:
/// </para>
/// <list type="bullet">
///   <item><c>"organization"</c> — scoped to <see cref="OrganizationId"/>
///   only; available to all teams in the org.</item>
///   <item><c>"team"</c> — scoped to a specific
///   <c>(OrganizationId, TeamId)</c> pair.</item>
/// </list>
/// <para>Backed by the <c>core_partitions</c> table.</para>
/// </remarks>
public sealed class Partition : DomainEntityBase, IOrganizationScopedEntity
{
    /// <summary>
    /// Parent scope: <c>"organization"</c> or <c>"team"</c>.
    /// </summary>
    public required string ScopeType { get; init; }

    /// <summary>
    /// Owning organization. Always set, even for team-scoped partitions.
    /// </summary>
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Owning team; <see langword="null"/> for organization-scoped
    /// partitions.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Human-readable partition name.
    /// </summary>
    public required string DisplayName { get; set; }
}
