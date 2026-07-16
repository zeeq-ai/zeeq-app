using Zeeq.Core.Models;

namespace Zeeq.Testing.EntityGraphs;

/// <summary>
/// Seed graph used by backend tests that need an organization, root team, and
/// active members.
/// </summary>
public sealed class SeedContext
{
    private SeedContext(
        User[] users,
        Organization organization,
        Team rootTeam,
        OrganizationMembership[] organizationMemberships,
        TeamMembership[] teamMemberships
    )
    {
        Users = users;
        Organization = organization;
        RootTeam = rootTeam;
        OrganizationMemberships = organizationMemberships;
        TeamMemberships = teamMemberships;
    }

    /// <summary>
    /// Owner user for the generated organization.
    /// </summary>
    public User Owner => Users[0];

    /// <summary>
    /// Generated organization.
    /// </summary>
    public Organization Organization { get; }

    /// <summary>
    /// Root team for the generated organization.
    /// </summary>
    public Team RootTeam { get; }

    /// <summary>
    /// Users created with the seed graph.
    /// </summary>
    public IReadOnlyList<User> Users { get; }

    /// <summary>
    /// Active organization memberships created for <see cref="Users"/>.
    /// </summary>
    public IReadOnlyList<OrganizationMembership> OrganizationMemberships { get; }

    /// <summary>
    /// Root-team memberships created for <see cref="Users"/>.
    /// </summary>
    public IReadOnlyList<TeamMembership> TeamMemberships { get; }

    /// <summary>
    /// Creates a prefixed UUIDv7-backed test identifier.
    /// </summary>
    /// <param name="prefix">Identifier prefix such as <c>user</c> or <c>org</c>.</param>
    /// <returns>A stable-shaped test identifier.</returns>
    public static string NewId(string prefix) => $"{prefix}_{Guid.CreateVersion7():N}";

    /// <summary>
    /// Generates a default seed graph with the requested number of active users.
    /// </summary>
    /// <param name="userCount">Number of active users to create. The first user is the owner.</param>
    /// <returns>A generated seed context.</returns>
    public static SeedContext Generate(int userCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(userCount, 1);

        var userSetupActions = Enumerable
            .Range(0, userCount)
            .Select(_ => (Action<User>)(_ => { }))
            .ToArray();

        return Generate(organizationSetup: null, userSetupActions);
    }

    /// <summary>
    /// Generates a seed graph with one user per setup action.
    /// </summary>
    /// <param name="userSetupActions">Per-user customization actions.</param>
    /// <returns>A generated seed context.</returns>
    public static SeedContext Generate(params Action<User>[] userSetupActions) =>
        Generate(organizationSetup: null, userSetupActions);

    /// <summary>
    /// Generates a seed graph with organization and user customization.
    /// </summary>
    /// <param name="organizationSetup">Organization customization action.</param>
    /// <param name="userSetupActions">Per-user customization actions.</param>
    /// <returns>A generated seed context.</returns>
    public static SeedContext Generate(
        Action<Organization>? organizationSetup,
        params Action<User>[] userSetupActions
    )
    {
        if (userSetupActions.Length == 0)
        {
            userSetupActions = [_ => { }];
        }

        var now = DateTimeOffset.UtcNow;
        var users = new User[userSetupActions.Length];

        for (var i = 0; i < users.Length; i++)
        {
            var userId = NewId("user");
            var user = new User
            {
                Id = userId,
                DisplayName = $"Test User {i + 1}",
                Email = $"{userId}@example.test",
                EmailVerified = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            userSetupActions[i].Invoke(user);
            users[i] = user;
        }

        var organizationId = NewId("org");
        var slugSuffix = organizationId[^8..];
        var organization = new Organization
        {
            Id = organizationId,
            DisplayName = "Test Organization",
            Slug = $"test-organization-{slugSuffix}",
            CreatedByUserId = users[0].Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ActivatedAtUtc = now,
        };
        organizationSetup?.Invoke(organization);

        var rootTeam = new Team
        {
            Id = NewId("team"),
            OrganizationId = organization.Id,
            DisplayName = "Root Team",
            IsRootTeam = true,
            CreatedByUserId = users[0].Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var organizationMemberships = users
            .Select(
                (user, index) =>
                    new OrganizationMembership
                    {
                        Id = NewId("mem"),
                        OrganizationId = organization.Id,
                        UserId = user.Id,
                        Role = index == 0 ? "owner" : "member",
                        Status = MembershipStatus.Active,
                        IsDefault = index == 0,
                        CreatedByUserId = users[0].Id,
                        CreatedAtUtc = now,
                    }
            )
            .ToArray();

        var teamMemberships = users
            .Select(
                (user, index) =>
                    new TeamMembership
                    {
                        OrganizationId = organization.Id,
                        TeamId = rootTeam.Id,
                        UserId = user.Id,
                        Role = index == 0 ? "owner" : "member",
                        CreatedByUserId = users[0].Id,
                        CreatedAtUtc = now,
                    }
            )
            .ToArray();

        return new SeedContext(
            users,
            organization,
            rootTeam,
            organizationMemberships,
            teamMemberships
        );
    }

    /// <summary>
    /// Returns seed entities in foreign-key-safe insertion order.
    /// </summary>
    /// <returns>Seed entities to add to EF Core.</returns>
    public IReadOnlyList<object> GetSeedEntities() =>
        [.. Users, Organization, RootTeam, .. OrganizationMemberships, .. TeamMemberships];
}
