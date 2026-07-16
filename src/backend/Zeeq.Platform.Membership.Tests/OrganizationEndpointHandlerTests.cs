using Zeeq.Core.Models;
using Zeeq.Testing.EntityGraphs;
using Microsoft.AspNetCore.Http.HttpResults;

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
}
