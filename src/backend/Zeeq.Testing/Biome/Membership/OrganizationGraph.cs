using Zeeq.Biome;
using Zeeq.Core.Models;

namespace Zeeq.Testing.Biomes;

/// <summary>
/// Organization aggregate returned by <see cref="MembershipBlueprints"/>.
/// </summary>
/// <param name="Organization">Generated organization.</param>
/// <param name="RootTeam">Generated root team.</param>
/// <param name="OrganizationMembership">Generated owner organization membership.</param>
/// <param name="RootTeamMembership">Generated owner root-team membership.</param>
public sealed record OrganizationGraph(
    Organization Organization,
    Team RootTeam,
    OrganizationMembership OrganizationMembership,
    TeamMembership RootTeamMembership
) : IBiomeAggregate
{
    /// <inheritdoc />
    IEnumerable<object> IBiomeAggregate.GetEntities() => GetEntities();

    /// <summary>
    /// Returns generated rows in foreign-key-safe insertion order.
    /// </summary>
    /// <returns>Rows that make up the organization graph.</returns>
    public IReadOnlyList<object> GetEntities() =>
        [Organization, RootTeam, OrganizationMembership, RootTeamMembership];
}
