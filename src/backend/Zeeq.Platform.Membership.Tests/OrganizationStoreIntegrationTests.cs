using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Postgres integration tests for organization persistence and slug behavior.
///
/// Run all membership tests:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/OrganizationStoreIntegrationTests/*"
/// </summary>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class OrganizationStoreIntegrationTests(PgDatabaseFixture postgres)
    : MembershipIntegrationTestBase(postgres)
{
    [Test]
    public async Task FindOrganizationByIdAsync_Exists_ReturnsOrg()
    {
        var store = CreateStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                    organization.Slug = "acme";
                }
            )
            .BuildAsync();
        var org = seed.Organization;
        var found = await store.FindOrganizationByIdAsync(org.Id, CancellationToken.None);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Slug).IsEqualTo("acme");
    }

    [Test]
    public async Task FindOrganizationByIdAsync_Missing_ReturnsNull()
    {
        var store = CreateStore();
        var found = await store.FindOrganizationByIdAsync("nonexistent", CancellationToken.None);
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task FindOrganizationBySlugAsync_Exists_ReturnsOrg()
    {
        var store = CreateStore();
        await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                    organization.Slug = "my-org";
                }
            )
            .BuildAsync();
        var found = await store.FindOrganizationBySlugAsync("my-org", CancellationToken.None);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.DisplayName).IsEqualTo("Test Org");
    }

    [Test]
    public async Task FindOrganizationsByIdsAsync_WithMixedIds_ReturnsExistingOnly()
    {
        var store = CreateStore();
        var (seed, organizationGraphs) = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                    organization.Slug = "a";
                }
            )
            .AddOrganizations(organization =>
            {
                organization.DisplayName = "Test Org";
                organization.Slug = "b";
            })
            .BuildAsync();
        var orgA = seed.Organization;
        var orgB = organizationGraphs[0].Organization;

        var orgs = await store.FindOrganizationsByIdsAsync(
            [orgA.Id, orgB.Id, "nonexistent"],
            CancellationToken.None
        );

        await Assert.That(orgs).Count().IsEqualTo(2);
        await Assert.That(orgs.Select(o => o.Slug)).Contains("a");
        await Assert.That(orgs.Select(o => o.Slug)).Contains("b");
    }

    [Test]
    public async Task FindOrganizationActivationStateAsync_WithActiveOrg_ReturnsActivationState()
    {
        var store = CreateStore();
        var activatedAt = DateTimeOffset.UtcNow.TruncateToPostgresPrecision();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.ActivatedAtUtc = activatedAt;
                }
            )
            .BuildAsync();

        var state = await store.FindOrganizationActivationStateAsync(
            seed.Organization.Id,
            CancellationToken.None
        );

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.OrganizationId).IsEqualTo(seed.Organization.Id);
        await Assert.That(state.ActivatedAtUtc).IsEqualTo(activatedAt);
        await Assert.That(state.DisabledAtUtc).IsNull();
        await Assert.That(state.IsActive).IsTrue();
    }

    [Test]
    public async Task FindOrganizationActivationStateAsync_WithDisabledOrg_ReturnsInactiveState()
    {
        var store = CreateStore();
        var disabledAt = DateTimeOffset.UtcNow.TruncateToPostgresPrecision();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisabledAtUtc = disabledAt;
                }
            )
            .BuildAsync();

        var state = await store.FindOrganizationActivationStateAsync(
            seed.Organization.Id,
            CancellationToken.None
        );

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.ActivatedAtUtc).IsNotNull();
        await Assert.That(state.DisabledAtUtc).IsEqualTo(disabledAt);
        await Assert.That(state.IsActive).IsFalse();
    }

    [Test]
    public async Task FindOrganizationActivationStateAsync_WithMissingOrg_ReturnsNull()
    {
        var store = CreateStore();

        var state = await store.FindOrganizationActivationStateAsync(
            "org_missing",
            CancellationToken.None
        );

        await Assert.That(state).IsNull();
    }

    [Test]
    public async Task IsSlugAvailableAsync_UnusedSlug_ReturnsTrue()
    {
        var store = CreateStore();
        var available = await store.IsSlugAvailableAsync("new-slug", null, CancellationToken.None);
        await Assert.That(available).IsTrue();
    }

    [Test]
    public async Task IsSlugAvailableAsync_TakenSlug_ReturnsFalse()
    {
        var store = CreateStore();
        await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                    organization.Slug = "taken";
                }
            )
            .BuildAsync();
        var available = await store.IsSlugAvailableAsync("taken", null, CancellationToken.None);
        await Assert.That(available).IsFalse();
    }

    [Test]
    public async Task IsSlugAvailableAsync_OwnSlug_ReturnsTrue()
    {
        var store = CreateStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                    organization.Slug = "my-slug";
                }
            )
            .BuildAsync();
        var org = seed.Organization;
        var available = await store.IsSlugAvailableAsync("my-slug", org.Id, CancellationToken.None);
        await Assert.That(available).IsTrue();
    }

    [Test]
    public async Task UpdateOrganizationAsync_WithValidChanges_PersistsUpdates()
    {
        var store = CreateStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                    organization.Slug = "before";
                }
            )
            .BuildAsync();
        var org = seed.Organization;
        org.DisplayName = "Updated Org";
        org.Slug = "after";

        await store.UpdateOrganizationAsync(org, CancellationToken.None);

        var reloaded = await store.FindOrganizationByIdAsync(org.Id, CancellationToken.None);
        await Assert.That(reloaded!.DisplayName).IsEqualTo("Updated Org");
        await Assert.That(reloaded.Slug).IsEqualTo("after");
    }

    [Test]
    public async Task FindUserEmailByIdAsync_WithExistingUser_ReturnsEmail()
    {
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        var email = await store.FindUserEmailByIdAsync(seed.Owner.Id, CancellationToken.None);

        await Assert.That(email).IsEqualTo(seed.Owner.Email);
    }

    [Test]
    public async Task FindUserEmailsByIdsAsync_WithExistingUsers_ReturnsEmailsByUserId()
    {
        var store = CreateStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context, userCount: 2).BuildAsync();

        var emails = await store.FindUserEmailsByIdsAsync(
            [seed.Users[0].Id, seed.Users[1].Id, "missing_user"],
            CancellationToken.None
        );

        await Assert.That(emails).Count().IsEqualTo(2);
        await Assert.That(emails[seed.Users[0].Id]).IsEqualTo(seed.Users[0].Email);
        await Assert.That(emails[seed.Users[1].Id]).IsEqualTo(seed.Users[1].Email);
    }

    [Test]
    public async Task IsAutoInviteSameDomainAvailableAsync_WithClaimedDomain_ReturnsFalse()
    {
        var store = CreateStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.AutoInviteSameDomainEnabled = true;
                    organization.AutoInviteSameDomain = "example.com";
                    organization.AutoInviteDefaultRole = "member";
                }
            )
            .BuildAsync();

        var availableForOtherOrg = await store.IsAutoInviteSameDomainAvailableAsync(
            "example.com",
            "org_other",
            CancellationToken.None
        );
        var availableForSameOrg = await store.IsAutoInviteSameDomainAvailableAsync(
            "example.com",
            seed.Organization.Id,
            CancellationToken.None
        );

        await Assert.That(availableForOtherOrg).IsFalse();
        await Assert.That(availableForSameOrg).IsTrue();
    }

    [Test]
    public async Task OrganizationSlugUniqueIndex_WithDuplicateSlug_ThrowsDbUpdateException()
    {
        var slug = "duplicate-" + Guid.NewGuid().ToString("N");
        await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Test Org";
                    organization.Slug = slug;
                }
            )
            .BuildAsync();

        _context.Organizations.Add(
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "Duplicate Org",
                Slug = slug,
                CreatedByUserId = "test_user",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );

        var saveFailed = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            saveFailed = true;
        }

        await Assert.That(saveFailed).IsTrue();
    }

    [Test]
    public async Task OrganizationAutoInviteSameDomainUniqueIndex_WithDuplicateEnabledDomain_ThrowsDbUpdateException()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedUser(_context, "same_domain_owner_1");
        await SeedUser(_context, "same_domain_owner_2");

        _context.Organizations.AddRange(
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "First Org",
                CreatedByUserId = "same_domain_owner_1",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                AutoInviteSameDomainEnabled = true,
                AutoInviteSameDomain = "example.com",
                AutoInviteDefaultRole = "member",
            },
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "Second Org",
                CreatedByUserId = "same_domain_owner_2",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                AutoInviteSameDomainEnabled = true,
                AutoInviteSameDomain = "example.com",
                AutoInviteDefaultRole = "member",
            }
        );

        var saveFailed = false;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            saveFailed = true;
        }

        await Assert.That(saveFailed).IsTrue();
    }

    [Test]
    public async Task OrganizationAutoInviteSameDomainUniqueIndex_WithDisabledOrganization_AllowsReuse()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedUser(_context, "disabled_domain_owner_1");
        await SeedUser(_context, "disabled_domain_owner_2");

        _context.Organizations.AddRange(
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "Disabled Org",
                CreatedByUserId = "disabled_domain_owner_1",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                DisabledAtUtc = now,
                AutoInviteSameDomainEnabled = true,
                AutoInviteSameDomain = "example.com",
                AutoInviteDefaultRole = "member",
            },
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "Replacement Org",
                CreatedByUserId = "disabled_domain_owner_2",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                AutoInviteSameDomainEnabled = true,
                AutoInviteSameDomain = "example.com",
                AutoInviteDefaultRole = "member",
            }
        );

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task UpdateOrganizationSameDomainOnboardingAsync_WithDuplicateDomain_ReturnsFalse()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var targetOrgId = NewId("org");
        await SeedUser(_context, "conflict_domain_owner_1");
        await SeedUser(_context, "conflict_domain_owner_2");

        _context.Organizations.AddRange(
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "Claimed Org",
                CreatedByUserId = "conflict_domain_owner_1",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                AutoInviteSameDomainEnabled = true,
                AutoInviteSameDomain = "example.com",
                AutoInviteDefaultRole = "member",
            },
            new Organization
            {
                Id = targetOrgId,
                DisplayName = "Target Org",
                CreatedByUserId = "conflict_domain_owner_2",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                AutoInviteDefaultRole = "member",
            }
        );
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var target =
            await store.FindOrganizationByIdAsync(targetOrgId, CancellationToken.None)
            ?? throw new InvalidOperationException("Target org was not seeded.");
        target.AutoInviteSameDomainEnabled = true;
        target.AutoInviteSameDomain = "example.com";
        target.AutoInviteDefaultRole = "member";

        var updated = await store.UpdateOrganizationSameDomainOnboardingAsync(
            target,
            CancellationToken.None
        );

        await Assert.That(updated).IsFalse();

        var reloaded = await store.FindOrganizationByIdAsync(targetOrgId, CancellationToken.None);
        await Assert.That(reloaded!.AutoInviteSameDomainEnabled).IsFalse();
        await Assert.That(reloaded.AutoInviteSameDomain).IsNull();
    }
}
