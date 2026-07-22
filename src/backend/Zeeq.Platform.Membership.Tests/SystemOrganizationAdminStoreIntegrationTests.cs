using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Postgres integration tests for system organization admin persistence.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.Membership.Tests --output detailed --disable-logo --treenode-filter "/*/*/SystemOrganizationAdminStoreIntegrationTests/*"
/// </summary>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SystemOrganizationAdminStoreIntegrationTests(PgDatabaseFixture postgres)
    : MembershipIntegrationTestBase(postgres)
{
    [Test]
    public async Task ListOrganizationsAsync_ReturnsCreatorMemberCountsTierAndLlmSummary()
    {
        var store = CreateSystemStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Acme Platform";
                    organization.Slug = "acme-platform";
                    organization.Tier = OrganizationTier.Priority;
                    organization.LlmConfiguration = TestLlmConfiguration();
                }
            )
            .BuildAsync();
        var extraUserId = NewId("user");
        await SeedUser(_context, extraUserId);
        _context.OrganizationMemberships.Add(
            new OrganizationMembership
            {
                Id = NewId("membership"),
                OrganizationId = seed.Organization.Id,
                UserId = extraUserId,
                Role = "member",
                Status = MembershipStatus.Active,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        _context.OrganizationMemberships.Add(
            new OrganizationMembership
            {
                Id = NewId("membership"),
                OrganizationId = seed.Organization.Id,
                UserId = null,
                InvitedEmail = "pending@example.com",
                Role = "member",
                Status = MembershipStatus.Pending,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            }
        );
        await _context.SaveChangesAsync();

        var page = await store.ListOrganizationsAsync(1, 25, "acme", CancellationToken.None);
        var organization = page.Items.Single();

        await Assert.That(page.TotalCount).IsEqualTo(1);
        await Assert.That(organization.Creator.Email).IsEqualTo(seed.Owner.Email);
        await Assert.That(organization.MemberCount).IsEqualTo(2);
        await Assert.That(organization.Tier).IsEqualTo(OrganizationTier.Priority);
        await Assert.That(organization.LlmConfiguration.Fast.UsesManagedKey).IsTrue();
        await Assert.That(organization.LlmConfiguration.Fast.KeyId).IsEqualTo("key_fast");
    }

    [Test]
    public async Task ListOrganizationsAsync_MatchesDisplayNameIdAndSlug()
    {
        var store = CreateSystemStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.DisplayName = "Northwind Research";
                    organization.Slug = "northwind";
                }
            )
            .BuildAsync();

        var byName = await store.ListOrganizationsAsync(1, 25, "research", CancellationToken.None);
        var byId = await store.ListOrganizationsAsync(
            1,
            25,
            seed.Organization.Id[^12..],
            CancellationToken.None
        );
        var bySlug = await store.ListOrganizationsAsync(1, 25, "NORTHWIND", CancellationToken.None);

        await Assert
            .That(byName.Items.Select(organization => organization.Id))
            .Contains(seed.Organization.Id);
        await Assert
            .That(byId.Items.Select(organization => organization.Id))
            .Contains(seed.Organization.Id);
        await Assert
            .That(bySlug.Items.Select(organization => organization.Id))
            .Contains(seed.Organization.Id);
    }

    [Test]
    public async Task ListOrganizationsAsync_TreatsWildcardCharactersAsLiteralText()
    {
        var store = CreateSystemStore();
        _context.Organizations.AddRange(
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "100% Real",
                CreatedByUserId = "test_user",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
            new Organization
            {
                Id = NewId("org"),
                DisplayName = "Plain Real",
                CreatedByUserId = "test_user",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }
        );
        await _context.SaveChangesAsync();

        var page = await store.ListOrganizationsAsync(1, 25, "%", CancellationToken.None);

        await Assert.That(page.Items).Count().IsEqualTo(1);
        await Assert.That(page.Items.Single().DisplayName).IsEqualTo("100% Real");
    }

    [Test]
    public async Task ListOrganizationsAsync_WithScopedRows_ReturnsDeterministicPageAndTotal()
    {
        var store = CreateSystemStore();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1).TruncateToPostgresPrecision();
        var uniqueName = NewId("paging");
        _context.Organizations.AddRange(
            new Organization
            {
                Id = NewId("org"),
                DisplayName = $"{uniqueName} First",
                CreatedByUserId = "test_user",
                CreatedAtUtc = baseTime,
            },
            new Organization
            {
                Id = NewId("org"),
                DisplayName = $"{uniqueName} Second",
                CreatedByUserId = "test_user",
                CreatedAtUtc = baseTime.AddMinutes(1),
            },
            new Organization
            {
                Id = NewId("org"),
                DisplayName = $"{uniqueName} Third",
                CreatedByUserId = "test_user",
                CreatedAtUtc = baseTime.AddMinutes(2),
            }
        );
        await _context.SaveChangesAsync();

        var page = await store.ListOrganizationsAsync(2, 1, uniqueName, CancellationToken.None);

        await Assert.That(page.TotalCount).IsEqualTo(3);
        await Assert.That(page.Items).Count().IsEqualTo(1);
        await Assert.That(page.Items.Single().DisplayName).IsEqualTo($"{uniqueName} Second");
    }

    [Test]
    public async Task ListOrganizationsAsync_PageTooLarge_ThrowsArgumentOutOfRangeException()
    {
        var store = CreateSystemStore();
        async Task Act() =>
            await store.ListOrganizationsAsync(10_001, 25, null, CancellationToken.None);

        await Assert.That(Act).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ListMembersAsync_ReturnsActiveMembersOnly()
    {
        var store = CreateSystemStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var extraUserId = NewId("user");
        var disabledUserId = NewId("user");
        await SeedUser(_context, extraUserId);
        await SeedUser(_context, disabledUserId);
        _context.OrganizationMemberships.AddRange(
            new OrganizationMembership
            {
                Id = NewId("membership"),
                OrganizationId = seed.Organization.Id,
                UserId = extraUserId,
                Role = "admin",
                Status = MembershipStatus.Active,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            },
            new OrganizationMembership
            {
                Id = NewId("membership"),
                OrganizationId = seed.Organization.Id,
                UserId = null,
                InvitedEmail = "pending@example.com",
                Role = "member",
                Status = MembershipStatus.Pending,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            },
            new OrganizationMembership
            {
                Id = NewId("membership"),
                OrganizationId = seed.Organization.Id,
                UserId = disabledUserId,
                Role = "member",
                Status = MembershipStatus.Active,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                DisabledAtUtc = DateTimeOffset.UtcNow,
            }
        );
        await _context.SaveChangesAsync();

        var members = await store.ListMembersAsync(
            seed.Organization.Id,
            1,
            25,
            CancellationToken.None
        );

        await Assert.That(members.TotalCount).IsEqualTo(2);
        await Assert.That(members.Items.Select(member => member.UserId)).Contains(seed.Owner.Id);
        await Assert.That(members.Items.Select(member => member.UserId)).Contains(extraUserId);
        await Assert
            .That(members.Items.Select(member => member.UserId))
            .DoesNotContain(disabledUserId);
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_ActivateSetsActivatedAndClearsDisabled()
    {
        var store = CreateSystemStore();
        var disabledAt = DateTimeOffset.UtcNow.AddMinutes(-10).TruncateToPostgresPrecision();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.ActivatedAtUtc = null;
                    organization.DisabledAtUtc = disabledAt;
                }
            )
            .BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: null,
            CancellationToken.None
        );

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.ActivatedAtUtc).IsNotNull();
        await Assert.That(updated.DisabledAtUtc).IsNull();
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_RepeatedActivatePreservesActivationTimestamp()
    {
        var store = CreateSystemStore();
        var activatedAt = DateTimeOffset.UtcNow.AddMinutes(-10).TruncateToPostgresPrecision();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.ActivatedAtUtc = activatedAt;
                    organization.DisabledAtUtc = null;
                }
            )
            .BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: null,
            CancellationToken.None
        );

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.ActivatedAtUtc).IsEqualTo(activatedAt);
        await Assert.That(updated.DisabledAtUtc).IsNull();
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_DeactivateClearsActivatedAndSetsDisabled()
    {
        var store = CreateSystemStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: false,
            tier: null,
            CancellationToken.None
        );

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.ActivatedAtUtc).IsNull();
        await Assert.That(updated.DisabledAtUtc).IsNotNull();
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_TierUpdatePersists()
    {
        var store = CreateSystemStore();
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: null,
            tier: OrganizationTier.Low,
            CancellationToken.None
        );

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(OrganizationTier.Low);
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_CombinedActivateAndTierPersistsBoth()
    {
        var store = CreateSystemStore();
        var seed = await EntityGraph
            .AddGeneratedSeed(
                _context,
                organization =>
                {
                    organization.ActivatedAtUtc = null;
                    organization.DisabledAtUtc = DateTimeOffset.UtcNow;
                    organization.Tier = OrganizationTier.Default;
                }
            )
            .BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: OrganizationTier.Priority,
            CancellationToken.None
        );

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.ActivatedAtUtc).IsNotNull();
        await Assert.That(updated.DisabledAtUtc).IsNull();
        await Assert.That(updated.Tier).IsEqualTo(OrganizationTier.Priority);
    }

    private PostgresSystemOrganizationManagementStore CreateSystemStore() => new(_context);

    private static OrganizationLlmConfiguration TestLlmConfiguration() =>
        new()
        {
            Fast = new OrganizationLlmTierConfiguration
            {
                Provider = "OpenAI",
                Model = "gpt-5-mini",
                KeyId = "key_fast",
            },
            High = new OrganizationLlmTierConfiguration
            {
                Provider = "Fireworks",
                Model = "accounts/fireworks/models/deepseek-v4-pro",
            },
            Max = new OrganizationLlmTierConfiguration
            {
                Provider = "Fireworks",
                Model = "accounts/fireworks/models/glm-5p2",
            },
        };
}
