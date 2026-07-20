using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Creates a new organization for the authenticated user.
/// </summary>
public sealed class CreateOrganizationHandler(IZeeqMembershipStore store) : IEndpointHandler
{
    private const int MaxCreatedOrganizations = 5;

    /// <summary>
    /// Creates the organization, root team, owner membership, and root team
    /// membership. Users may create at most five organizations.
    /// </summary>
    public async Task<Results<Created<OrganizationResponse>, ValidationProblem>> HandleAsync(
        CreateOrganizationRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject)!;
        var displayName = request.DisplayName.Trim();

        if (displayName.Length == 0)
        {
            return ValidationProblem("displayName", "Display name is required.");
        }

        if (displayName.Length > 200)
        {
            return ValidationProblem(
                "displayName",
                "Display name must be 200 characters or fewer."
            );
        }

        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N");
        var organizationId = "org_" + suffix;
        var teamId = "team_" + suffix;
        var slug = OrganizationSlugGenerator.Create(displayName, organizationId);

        var organization = new Organization
        {
            Id = organizationId,
            DisplayName = displayName,
            Slug = slug,
            IconUrl = request.IconUrl,
            CreatedByUserId = userId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ActivatedAtUtc = now,
        };
        var rootTeam = new Team
        {
            Id = teamId,
            OrganizationId = organizationId,
            DisplayName = displayName + " Team",
            IsRootTeam = true,
            CreatedByUserId = userId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var ownerMembership = new OrganizationMembership
        {
            Id = "mem_" + suffix,
            OrganizationId = organizationId,
            UserId = userId,
            Role = "owner",
            Status = MembershipStatus.Active,
            CreatedByUserId = userId,
            CreatedAtUtc = now,
        };
        var rootTeamMembership = new TeamMembership
        {
            OrganizationId = organizationId,
            TeamId = teamId,
            UserId = userId,
            Role = "owner",
            CreatedByUserId = userId,
            CreatedAtUtc = now,
        };

        var created = await store.CreateOrganizationAsync(
            organization,
            rootTeam,
            ownerMembership,
            rootTeamMembership,
            MaxCreatedOrganizations,
            ct
        );

        if (created is null)
        {
            return ValidationProblem("organization", "You can create up to 5 organizations.");
        }

        var creatorEmail =
            user.FindFirstValue(OpenIddictConstants.Claims.Email)
            ?? user.FindFirstValue(ClaimTypes.Email);
        var creatorDomain = EmailDomainNormalizer.FromEmail(creatorEmail);
        var domainAvailable =
            creatorDomain is null
            || await store.IsAutoInviteSameDomainAvailableAsync(creatorDomain, created.Id, ct);
        var sameDomainStatus = OrganizationSameDomainOnboardingStatusFactory.Create(
            created,
            creatorEmail,
            domainAvailable
        );

        return TypedResults.Created(
            $"/api/v1/orgs/{created.Id}",
            OrganizationSameDomainOnboardingStatusFactory.ToOrganizationResponse(
                created,
                ownerMembership.Role,
                sameDomainStatus
            )
        );
    }

    private static ValidationProblem ValidationProblem(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
