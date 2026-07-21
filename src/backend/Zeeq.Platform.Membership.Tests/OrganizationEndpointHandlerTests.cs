using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Models;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Handler unit tests for organization endpoint validation and membership gates.
///
/// Run all membership tests:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/OrganizationEndpointHandlerTests/*"
/// </summary>
public sealed class OrganizationEndpointHandlerTests
{
    [Test]
    public async Task CreateOrganizationHandler_WithFiveCreatedOrganizations_ReturnsValidationProblem()
    {
        var (seed, organizationGraphs) = await EntityGraph
            .AddGeneratedSeed()
            .AddOrganizations(4)
            .BuildAsync();
        var store = new TestMembershipStore()
            .AddSeed(seed)
            .AddOrganizationGraphs(organizationGraphs);

        // Guards that the create endpoint enforces the product limit of five
        // organizations created by a single user.
        var handler = new CreateOrganizationHandler(store);
        var result = await handler.HandleAsync(
            new CreateOrganizationRequest("Sixth Org", null, null),
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(store.Organizations).Count().IsEqualTo(5);
    }

    [Test]
    public async Task CreateOrganizationHandler_WithValidRequest_CreatesOwnerOrganization()
    {
        var store = new TestMembershipStore();

        // Guards that creating an organization inserts the org, root team,
        // active owner membership, and root team membership together.
        var handler = new CreateOrganizationHandler(store);
        var result = await handler.HandleAsync(
            new CreateOrganizationRequest("My New Org", null, null),
            MembershipTestClaims.TestUser("user_1"),
            CancellationToken.None
        );

        var created = result.Result as Created<OrganizationResponse>;

        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Value!.DisplayName).IsEqualTo("My New Org");
        await Assert.That(created.Value.Role).IsEqualTo("owner");
        await Assert.That(created.Value.Slug).StartsWith("my-new-org-");
        await Assert.That(store.Organizations).Count().IsEqualTo(1);
        await Assert.That(store.Teams).Count().IsEqualTo(1);
        await Assert.That(store.Teams[0].IsRootTeam).IsTrue();
        await Assert.That(store.Memberships).Count().IsEqualTo(1);
        await Assert.That(store.Memberships[0].Status).IsEqualTo(MembershipStatus.Active);
        await Assert.That(store.TeamMemberships).Count().IsEqualTo(1);
    }

    [Test]
    public async Task CheckSlugHandler_InvalidSlug_ReturnsBadRequest()
    {
        // Guards that slugs with spaces or uppercase characters are rejected
        // with a BadRequest before any store lookup.
        var handler = new CheckSlugHandler(new TestMembershipStore());

        var result = await handler.HandleAsync("Bad Slug", null, CancellationToken.None);

        await Assert.That(result.Result is BadRequest).IsTrue();
    }

    [Test]
    public async Task UpdateOrganizationHandler_DuplicateSlug_ReturnsValidationProblem()
    {
        var (seed, organizationGraphs) = await EntityGraph
            .AddGeneratedSeed(
                null,
                organization =>
                {
                    organization.DisplayName = "Target";
                    organization.Slug = "target";
                }
            )
            .AddOrganizations(organization =>
            {
                organization.DisplayName = "Taken";
                organization.Slug = "taken";
            })
            .BuildAsync();
        var org = seed.Organization;
        var store = new TestMembershipStore()
            .AddSeed(seed)
            .AddOrganizationGraphs(organizationGraphs);

        // Guards that setting an org slug to one already taken by another org
        // returns ValidationProblem and does not modify the original slug.
        var handler = new UpdateOrganizationHandler(store);
        var result = await handler.HandleAsync(
            org.Id,
            new UpdateOrganizationRequest(null, "taken", null),
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(org.Slug).IsEqualTo("target");
    }

    [Test]
    public async Task UpdateOrganizationHandler_NullIconUrl_ClearsIcon()
    {
        var seed = await EntityGraph
            .AddGeneratedSeed(
                null,
                organization =>
                {
                    organization.DisplayName = "Target";
                    organization.Slug = "target";
                    organization.IconUrl = "data:image/png;base64,abc";
                }
            )
            .BuildAsync();
        var org = seed.Organization;
        var store = new TestMembershipStore().AddSeed(seed);

        // Guards that the settings form's null icon URL clears the
        // organization's icon.
        var handler = new UpdateOrganizationHandler(store);
        var result = await handler.HandleAsync(
            org.Id,
            new UpdateOrganizationRequest(null, null, null),
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        var ok = result.Result as Ok<OrganizationResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.IconUrl).IsNull();
        await Assert.That(org.IconUrl).IsNull();
    }

    [Test]
    public async Task UpdateSameDomainOnboardingHandler_InvalidRole_ReturnsValidationProblem()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        seed.Owner.Email = "owner@example.com";
        var store = new TestMembershipStore().AddSeed(seed);
        var handler = new UpdateSameDomainOnboardingHandler(store);

        var result = await handler.HandleAsync(
            seed.Organization.Id,
            new UpdateSameDomainOnboardingRequest { Enabled = true, DefaultRole = "owner" },
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(seed.Organization.AutoInviteSameDomainEnabled).IsFalse();
    }

    [Test]
    public async Task UpdateSameDomainOnboardingHandler_MissingEnabled_ReturnsValidationProblem()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        seed.Owner.Email = "owner@example.com";
        var store = new TestMembershipStore().AddSeed(seed);
        var handler = new UpdateSameDomainOnboardingHandler(store);

        var result = await handler.HandleAsync(
            seed.Organization.Id,
            new UpdateSameDomainOnboardingRequest { DefaultRole = "member" },
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(seed.Organization.AutoInviteSameDomainEnabled).IsFalse();
    }

    [Test]
    public async Task UpdateSameDomainOnboardingHandler_Enable_WithPublicCreatorDomain_ReturnsValidationProblem()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        seed.Owner.Email = "owner@gmail.com";
        var store = new TestMembershipStore().AddSeed(seed);
        var handler = new UpdateSameDomainOnboardingHandler(store);

        var result = await handler.HandleAsync(
            seed.Organization.Id,
            new UpdateSameDomainOnboardingRequest { Enabled = true, DefaultRole = "member" },
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(seed.Organization.AutoInviteSameDomainEnabled).IsFalse();
        await Assert.That(seed.Organization.AutoInviteSameDomain).IsNull();
    }

    [Test]
    public async Task UpdateSameDomainOnboardingHandler_Enable_WithDuplicateDomain_ReturnsValidationProblem()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        seed.Owner.Email = "owner@example.com";
        var store = new TestMembershipStore().AddSeed(seed);
        store.Organizations.Add(
            new Organization
            {
                Id = "org_conflict",
                DisplayName = "Conflict",
                CreatedByUserId = "user_conflict",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AutoInviteSameDomainEnabled = true,
                AutoInviteSameDomain = "example.com",
                AutoInviteDefaultRole = "member",
            }
        );
        var handler = new UpdateSameDomainOnboardingHandler(store);

        var result = await handler.HandleAsync(
            seed.Organization.Id,
            new UpdateSameDomainOnboardingRequest { Enabled = true, DefaultRole = "admin" },
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(seed.Organization.AutoInviteSameDomainEnabled).IsFalse();
    }

    [Test]
    public async Task UpdateSameDomainOnboardingHandler_Enable_WithOmittedRole_DefaultsToMember()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        seed.Owner.Email = "owner@example.com";
        var store = new TestMembershipStore().AddSeed(seed);
        var handler = new UpdateSameDomainOnboardingHandler(store);

        var result = await handler.HandleAsync(
            seed.Organization.Id,
            new UpdateSameDomainOnboardingRequest { Enabled = true },
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );
        var ok = result.Result as Ok<SameDomainOnboardingStatusResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.DefaultRole).IsEqualTo("member");
        await Assert.That(seed.Organization.AutoInviteDefaultRole).IsEqualTo("member");
    }

    [Test]
    public async Task UpdateSameDomainOnboardingHandler_Disable_ClearsDomain()
    {
        var seed = await EntityGraph
            .AddGeneratedSeed(
                null,
                organization =>
                {
                    organization.AutoInviteSameDomainEnabled = true;
                    organization.AutoInviteSameDomain = "example.com";
                    organization.AutoInviteDefaultRole = "admin";
                }
            )
            .BuildAsync();
        seed.Owner.Email = "owner@example.com";
        var store = new TestMembershipStore().AddSeed(seed);
        var handler = new UpdateSameDomainOnboardingHandler(store);

        var result = await handler.HandleAsync(
            seed.Organization.Id,
            new UpdateSameDomainOnboardingRequest { Enabled = false, DefaultRole = "admin" },
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );
        var ok = result.Result as Ok<SameDomainOnboardingStatusResponse>;

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Enabled).IsFalse();
        await Assert.That(seed.Organization.AutoInviteSameDomainEnabled).IsFalse();
        await Assert.That(seed.Organization.AutoInviteSameDomain).IsNull();
        await Assert.That(seed.Organization.AutoInviteDefaultRole).IsEqualTo("member");
    }

    [Test]
    public async Task GetOrganizationsHandler_WithPrivateCreatorDomain_ReturnsCanEnable()
    {
        var seed = await EntityGraph.AddGeneratedSeed().BuildAsync();
        seed.Owner.Email = "owner@example.com";
        var store = new TestMembershipStore().AddSeed(seed);
        var handler = new GetOrganizationsHandler(store);

        var result = await handler.HandleAsync(
            MembershipTestClaims.TestUser(seed.Owner.Id),
            CancellationToken.None
        );
        var organization = result.Value!.Single();

        await Assert.That(organization.AutoInviteSameDomainCanEnable).IsTrue();
        await Assert.That(organization.AutoInviteSameDomain).IsEqualTo("example.com");
        await Assert.That(organization.AutoInviteSameDomainBlockReason).IsNull();
    }
}
