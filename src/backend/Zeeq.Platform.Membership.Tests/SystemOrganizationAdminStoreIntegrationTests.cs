using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

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
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.DisplayName = "Acme Platform";
                    organization.Slug = "acme-platform";
                    organization.Tier = OrganizationTier.Priority;
                    organization.LlmConfiguration = TestLlmConfiguration();
                },
                []
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
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.DisplayName = "Northwind Research";
                    organization.Slug = "northwind";
                },
                []
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
        await TestBiome.Start(_context, out var seed).BuildAsync();
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
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.ActivatedAtUtc = null;
                    organization.DisabledAtUtc = disabledAt;
                },
                []
            )
            .BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: null,
            actor: null,
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
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.ActivatedAtUtc = activatedAt;
                    organization.DisabledAtUtc = null;
                },
                []
            )
            .BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: null,
            actor: null,
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
        await TestBiome.Start(_context, out var seed).BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: false,
            tier: null,
            actor: null,
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
        await TestBiome.Start(_context, out var seed).BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: null,
            tier: OrganizationTier.Low,
            actor: null,
            CancellationToken.None
        );

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(OrganizationTier.Low);
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_CombinedActivateAndTierPersistsBoth()
    {
        var store = CreateSystemStore();
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.ActivatedAtUtc = null;
                    organization.DisabledAtUtc = DateTimeOffset.UtcNow;
                    organization.Tier = OrganizationTier.Default;
                },
                []
            )
            .BuildAsync();

        var updated = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: OrganizationTier.Priority,
            actor: null,
            CancellationToken.None
        );

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.ActivatedAtUtc).IsNotNull();
        await Assert.That(updated.DisabledAtUtc).IsNull();
        await Assert.That(updated.Tier).IsEqualTo(OrganizationTier.Priority);
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_Activate_RecordsOneAdminStateEventWithBeforeAndAfter()
    {
        var store = CreateSystemStore();
        var disabledAt = DateTimeOffset.UtcNow.AddMinutes(-10).TruncateToPostgresPrecision();
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.ActivatedAtUtc = null;
                    organization.DisabledAtUtc = disabledAt;
                    organization.Tier = OrganizationTier.Default;
                },
                []
            )
            .BuildAsync();

        await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: null,
            SystemAdminActor(),
            CancellationToken.None
        );

        var events = await ReadEventsAsync(seed.Organization.Id);
        var audit = events.Single();
        await Assert.That(audit.EventType).IsEqualTo("organization.admin_state_changed");
        await Assert.That(audit.ChangeType).IsEqualTo(nameof(AuditChangeType.Edited));
        await Assert.That(audit.SubjectType).IsEqualTo("organization");
        await Assert.That(audit.SubjectId).IsEqualTo(seed.Organization.Id);
        await Assert.That(audit.ChangedFields).IsEquivalentTo(new[] { "active" });
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot)!.Fields["active"])
            .IsEqualTo("false");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)!.Fields["active"])
            .IsEqualTo("true");
        await Assert.That(audit.ActorKind).IsEqualTo(AuditActorKinds.SystemAdmin);
        await Assert.That(audit.ActorChannel).IsEqualTo(AuditChannels.SystemConsole);
        await Assert.That(audit.ActorId).IsEqualTo("admin_1");
        await Assert.That(audit.SurfaceInSystem).IsTrue();
        await Assert.That(await CountSystemEventsAsync(seed.Organization.Id)).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_RepeatedActivateAndSameTier_RecordsNothingAndKeepsUpdatedAt()
    {
        var store = CreateSystemStore();
        var activatedAt = DateTimeOffset.UtcNow.AddMinutes(-10).TruncateToPostgresPrecision();
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-5).TruncateToPostgresPrecision();
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.ActivatedAtUtc = activatedAt;
                    organization.DisabledAtUtc = null;
                    organization.Tier = OrganizationTier.Priority;
                    organization.UpdatedAtUtc = updatedAt;
                },
                []
            )
            .BuildAsync();

        var result = await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: true,
            tier: OrganizationTier.Priority,
            SystemAdminActor(),
            CancellationToken.None
        );

        await Assert.That(result!.UpdatedAtUtc).IsEqualTo(updatedAt);
        await Assert.That(await ReadEventsAsync(seed.Organization.Id)).IsEmpty();
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_ActiveAndTierChange_RecordsBothChangedFieldsInOneEvent()
    {
        var store = CreateSystemStore();
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.ActivatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
                    organization.DisabledAtUtc = null;
                    organization.Tier = OrganizationTier.Default;
                },
                []
            )
            .BuildAsync();

        await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: false,
            tier: OrganizationTier.Low,
            SystemAdminActor(),
            CancellationToken.None
        );

        var audit = (await ReadEventsAsync(seed.Organization.Id)).Single();
        await Assert.That(audit.ChangedFields).IsEquivalentTo(new[] { "active", "tier" });
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot)!.Fields["tier"])
            .IsEqualTo("Default");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)!.Fields["tier"])
            .IsEqualTo("Low");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)!.Fields["active"])
            .IsEqualTo("false");
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_MissingOrganization_ReturnsNullAndRecordsNothing()
    {
        var store = CreateSystemStore();
        var missingOrgId = NewId("org");

        var result = await store.UpdateOrganizationAdminStateAsync(
            missingOrgId,
            active: true,
            tier: null,
            SystemAdminActor(),
            CancellationToken.None
        );

        await Assert.That(result).IsNull();
        await Assert.That(await ReadEventsAsync(missingOrgId)).IsEmpty();
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_TrackedStaleOrganization_UsesDatabasePreimage()
    {
        var store = CreateSystemStore();
        await TestBiome.Start(_context, out var seed).BuildAsync();
        await _context
            .Organizations.Where(org => org.Id == seed.Organization.Id)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(org => org.Tier, OrganizationTier.Priority)
            );

        await store.UpdateOrganizationAdminStateAsync(
            seed.Organization.Id,
            active: null,
            tier: OrganizationTier.Low,
            SystemAdminActor(),
            CancellationToken.None
        );

        var audit = (await ReadEventsAsync(seed.Organization.Id)).Single();
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot)!.Fields["tier"])
            .IsEqualTo("Priority");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)!.Fields["tier"])
            .IsEqualTo("Low");
    }

    [Test]
    public async Task UpdateOrganizationAdminStateAsync_AuditWriterFails_DoesNotPersistStateChange()
    {
        var store = new PostgresSystemOrganizationManagementStore(
            _context,
            new ThrowingAuditWriter()
        );
        await TestBiome
            .Start(
                _context,
                out var seed,
                organization =>
                {
                    organization.ActivatedAtUtc = null;
                    organization.DisabledAtUtc = null;
                },
                []
            )
            .BuildAsync();

        await Assert
            .That(async () =>
                await store.UpdateOrganizationAdminStateAsync(
                    seed.Organization.Id,
                    active: true,
                    tier: null,
                    SystemAdminActor(),
                    CancellationToken.None
                )
            )
            .Throws<InvalidOperationException>();

        // Nothing was saved: the tracked entity was mutated in memory but SaveChanges never ran.
        _context.ChangeTracker.Clear();
        var persisted = await _context
            .Organizations.AsNoTracking()
            .SingleAsync(organization => organization.Id == seed.Organization.Id);
        await Assert.That(persisted.ActivatedAtUtc).IsNull();
    }

    private static AuditActor SystemAdminActor() =>
        new(
            AuditActorKinds.SystemAdmin,
            "admin_1",
            "Alice Admin",
            AuditChannels.SystemConsole,
            ResponsibleUserId: "admin_1"
        );

    private async Task<OrganizationAuditEvent[]> ReadEventsAsync(string organizationId) =>
        await _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(audit => audit.OrganizationId == organizationId)
            .OrderBy(audit => audit.OccurredAtUtc)
            .ToArrayAsync();

    private async Task<int> CountSystemEventsAsync(string organizationId) =>
        await _context
            .SystemAuditEvents.AsNoTracking()
            .CountAsync(audit => audit.SubjectId == organizationId);

    private sealed class ThrowingAuditWriter : IAuditTrailWriter
    {
        public void AppendOrganization(string organizationId, string? teamId, AuditChange change) =>
            throw new InvalidOperationException("audit append failed");

        public void AppendSystem(AuditChange change) =>
            throw new InvalidOperationException("audit append failed");
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
