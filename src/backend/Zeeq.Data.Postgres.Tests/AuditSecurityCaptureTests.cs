using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Auditing;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Guards durable security facts, no-op suppression, cross-scope attribution and secret exclusion.
/// Run: dotnet run --project src/backend/Zeeq.Data.Postgres.Tests -- --treenode-filter "/*/*/AuditSecurityCaptureTests/*"
/// </summary>
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuditSecurityCaptureTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task RoleChange_RecordsLockedPreimage_AndSuppressesRepeatedValue()
    {
        // A later replay of an identical value must not invent another change event.
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var before = await _context
            .OrganizationMemberships.AsNoTracking()
            .SingleAsync(m =>
                m.OrganizationId == seed.Organization.Id && m.UserId == seed.Owner.Id
            );
        var store = new PostgresZeeqMembershipStore(_context, Writer());

        await store.UpdateMemberRoleAsync(seed.Organization.Id, seed.Owner.Id, "admin", default);
        await store.UpdateMemberRoleAsync(seed.Organization.Id, seed.Owner.Id, "admin", default);

        var entry = await _context.Set<OrganizationAuditEvent>().AsNoTracking().SingleAsync();
        var beforeSnapshot = Snapshot(entry.BeforeSnapshot!);
        var afterSnapshot = Snapshot(entry.AfterSnapshot!);
        await Assert.That(entry.EventType).IsEqualTo("membership.role_changed");
        await Assert.That(entry.OrganizationId).IsEqualTo(seed.Organization.Id);
        await Assert.That(entry.ActorId).IsEqualTo("auditing_user");
        await Assert.That(beforeSnapshot.Fields["Role"]).IsEqualTo(before.Role);
        await Assert.That(afterSnapshot.Fields["Role"]).IsEqualTo("admin");
        await Assert.That(await _context.Set<SystemAuditEvent>().CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task MembershipAndFact_RollBackTogether_InAmbientTransaction()
    {
        // Both the set-based mutation and its tracked fact obey the caller's rollback boundary.
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var originalRole = await _context
            .OrganizationMemberships.Where(m =>
                m.OrganizationId == seed.Organization.Id && m.UserId == seed.Owner.Id
            )
            .Select(m => m.Role)
            .SingleAsync();
        var transaction = _context.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync("before_audited_role");

        await new PostgresZeeqMembershipStore(_context, Writer()).UpdateMemberRoleAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            "admin",
            default
        );
        await Assert.That(await _context.Set<OrganizationAuditEvent>().CountAsync()).IsEqualTo(1);
        await transaction.RollbackToSavepointAsync("before_audited_role");
        _context.ChangeTracker.Clear();

        var role = await _context
            .OrganizationMemberships.Where(m =>
                m.OrganizationId == seed.Organization.Id && m.UserId == seed.Owner.Id
            )
            .Select(m => m.Role)
            .SingleAsync();
        await Assert.That(role).IsEqualTo(originalRole);
        await Assert.That(await _context.Set<OrganizationAuditEvent>().CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task MemberRemoval_RecordsOneRelationshipDeletion_WithoutInvitationEmail()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var store = new PostgresZeeqMembershipStore(_context, Writer());
        await store.RemoveMemberAsync(seed.Organization.Id, seed.Owner.Id, default);
        await store.RemoveMemberAsync(seed.Organization.Id, seed.Owner.Id, default);

        var entry = await _context.Set<OrganizationAuditEvent>().AsNoTracking().SingleAsync();
        await Assert.That(entry.EventType).IsEqualTo("membership.removed");
        await Assert.That(entry.ChangeType).IsEqualTo(nameof(AuditChangeType.Deleted));
        await Assert.That(Snapshot(entry.BeforeSnapshot!).Fields["Status"]).IsEqualTo("Active");
        await Assert.That(Snapshot(entry.AfterSnapshot!).Fields["Status"]).IsEqualTo("Disabled");
        await Assert
            .That(Snapshot(entry.AfterSnapshot!).Fields.ContainsKey("InvitedEmail"))
            .IsFalse();
    }

    [Test]
    public async Task ActivationKeyRedemption_RecordsTwoLinkedFacts_WithoutSecretMaterial()
    {
        // The global key consumption and tenant activation are distinct facts in one operation.
        await TestBiome
            .Start(_context, out var seed, org => org.ActivatedAtUtc = null, [])
            .BuildAsync();
        var rawKey = OrganizationActivationKeyMaterial.GenerateKey();
        var hash = OrganizationActivationKeyMaterial.ComputeHash(rawKey);
        var store = new PostgresOrganizationActivationKeyStore(_context, Writer());
        var key = await store.CreateKeyAsync(
            new()
            {
                Id = "audit_key_" + Guid.NewGuid().ToString("N"),
                KeyHash = hash,
                Note = "operator-only-private-note",
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            },
            default
        );

        var result = await store.ConsumeKeyAndActivateOrganizationAsync(
            hash,
            seed.Organization.Id,
            seed.Owner.Id,
            default
        );
        var replay = await store.ConsumeKeyAndActivateOrganizationAsync(
            hash,
            seed.Organization.Id,
            seed.Owner.Id,
            default
        );

        var global = await _context.Set<SystemAuditEvent>().AsNoTracking().ToListAsync();
        var tenant = await _context.Set<OrganizationAuditEvent>().AsNoTracking().SingleAsync();
        var redeemed = global.Single(e => e.EventType == "activation_key.redeemed");
        var captured =
            System.Text.Json.JsonSerializer.Serialize(global)
            + tenant.BeforeSnapshot
            + tenant.AfterSnapshot;
        await Assert.That(result).IsEqualTo(OrganizationActivationExchangeResult.Activated);
        await Assert
            .That(replay)
            .IsEqualTo(OrganizationActivationExchangeResult.InvalidOrganization);
        await Assert.That(global.Count).IsEqualTo(2);
        await Assert.That(tenant.OperationId).IsEqualTo(redeemed.OperationId);
        await Assert.That(tenant.EventType).IsEqualTo("organization.activated");
        await Assert.That(tenant.SubjectId).IsEqualTo(seed.Organization.Id);
        await Assert.That(captured.Contains(rawKey, StringComparison.Ordinal)).IsFalse();
        await Assert.That(captured.Contains(hash, StringComparison.Ordinal)).IsFalse();
        await Assert.That(captured.Contains(key.Note!, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ActivationKeyRevocation_IsAnEdit_AndRepeatedRevocationAddsNothing()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var store = new PostgresOrganizationActivationKeyStore(_context, Writer());
        var key = await store.CreateKeyAsync(
            new()
            {
                Id = "audit_key_" + Guid.NewGuid().ToString("N"),
                KeyHash = OrganizationActivationKeyMaterial.ComputeHash(
                    OrganizationActivationKeyMaterial.GenerateKey()
                ),
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            },
            default
        );

        await store.RevokeKeyAsync(key.Id, default);
        await store.RevokeKeyAsync(key.Id, default);

        var entries = await _context.Set<SystemAuditEvent>().AsNoTracking().ToListAsync();
        var revoked = entries.Single(e => e.EventType == "activation_key.revoked");
        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(revoked.ChangeType).IsEqualTo(nameof(AuditChangeType.Edited));
        await Assert.That(Snapshot(revoked.AfterSnapshot!).Fields["Status"]).IsEqualTo("Revoked");
    }

    [Test]
    public async Task FailedRedemption_DoesNotLeaveFactsThatCanBeSavedAfterRollback()
    {
        await TestBiome
            .Start(_context, out var seed, org => org.ActivatedAtUtc = null, [])
            .BuildAsync();
        var hash = OrganizationActivationKeyMaterial.ComputeHash(
            OrganizationActivationKeyMaterial.GenerateKey()
        );
        var key = await new PostgresOrganizationActivationKeyStore(
            _context,
            Writer()
        ).CreateKeyAsync(
            new()
            {
                Id = "audit_key_" + Guid.NewGuid().ToString("N"),
                KeyHash = hash,
                CreatedByUserId = seed.Owner.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            },
            default
        );
        var store = new PostgresOrganizationActivationKeyStore(
            _context,
            new FailingOrganizationWriter(Writer())
        );

        await Assert
            .That(async () =>
                await store.ConsumeKeyAndActivateOrganizationAsync(
                    hash,
                    seed.Organization.Id,
                    seed.Owner.Id,
                    default
                )
            )
            .Throws<InvalidOperationException>();
        // Continuing the caller's transaction must not persist the already-attached global fact.
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await Assert.That(await _context.Set<OrganizationAuditEvent>().CountAsync()).IsEqualTo(0);
        await Assert.That(await _context.Set<SystemAuditEvent>().CountAsync()).IsEqualTo(1);
        await Assert
            .That(
                (
                    await _context.OrganizationActivationKeys.SingleAsync(k => k.Id == key.Id)
                ).ActivatedAtUtc
            )
            .IsNull();
        await Assert
            .That(
                (
                    await _context.Organizations.SingleAsync(o => o.Id == seed.Organization.Id)
                ).ActivatedAtUtc
            )
            .IsNull();
    }

    [Test]
    [Arguments("role", false)]
    [Arguments("remove", false)]
    [Arguments("leave", false)]
    [Arguments("role", true)]
    [Arguments("remove", true)]
    public async Task FailedMembershipMutation_CallerCanContinueWithoutFactOrMutation(
        string operation,
        bool failAfterSave
    )
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var original = await _context
            .OrganizationMemberships.AsNoTracking()
            .SingleAsync(row =>
                row.OrganizationId == seed.Organization.Id && row.UserId == seed.Owner.Id
            );
        var interceptor = new FailAfterSave { Enabled = failAfterSave };
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                _context.Database.GetDbConnection(),
                npgsql =>
                {
                    npgsql.SetPostgresVersion(18, 0);
                    npgsql.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new PostgresDbContext(options);
        await db.Database.UseTransactionAsync(
            _context.Database.CurrentTransaction!.GetDbTransaction()
        );
        var callerOrganization = await db.Organizations.SingleAsync(row =>
            row.Id == seed.Organization.Id
        );
        callerOrganization.DisplayName = "pending caller name";
        var writer = new PostgresAuditTrailWriter(db, new AuditActorContext());
        var store = new PostgresZeeqMembershipStore(
            db,
            failAfterSave ? writer : new AppendThenThrowWriter(writer)
        );
        await Assert
            .That(async () =>
            {
                if (operation == "role")
                    await store.UpdateMemberRoleAsync(
                        seed.Organization.Id,
                        seed.Owner.Id,
                        "admin",
                        default
                    );
                else if (operation == "remove")
                    await store.RemoveMemberAsync(seed.Organization.Id, seed.Owner.Id, default);
                else
                    await store.LeaveOrganizationAsync(
                        seed.Organization.Id,
                        seed.Owner.Id,
                        default
                    );
            })
            .Throws<InvalidOperationException>();
        interceptor.Enabled = false;
        await db.SaveChangesAsync();
        var saved = await _context
            .OrganizationMemberships.AsNoTracking()
            .SingleAsync(row => row.Id == original.Id);
        await Assert.That(saved.Role).IsEqualTo(original.Role);
        await Assert.That(saved.Status).IsEqualTo(original.Status);
        await Assert.That(saved.DisabledAtUtc).IsEqualTo(original.DisabledAtUtc);
        await Assert.That(await _context.OrganizationAuditEvents.CountAsync()).IsEqualTo(0);
        await Assert
            .That(
                (
                    await _context
                        .Organizations.AsNoTracking()
                        .SingleAsync(row => row.Id == seed.Organization.Id)
                ).DisplayName
            )
            .IsEqualTo("pending caller name");
    }

    [Test]
    [Arguments("create", false)]
    [Arguments("revoke", false)]
    [Arguments("admin", false)]
    [Arguments("create", true)]
    [Arguments("pretracked-create", false)]
    [Arguments("pretracked-create", true)]
    [Arguments("revoke", true)]
    [Arguments("admin", true)]
    public async Task FailedSecurityMutation_CallerCanContinueWithoutFactOrMutation(
        string operation,
        bool failAfterSave
    )
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var interceptor = new FailAfterSave();
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                _context.Database.GetDbConnection(),
                npgsql =>
                {
                    npgsql.SetPostgresVersion(18, 0);
                    npgsql.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new PostgresDbContext(options);
        await db.Database.UseTransactionAsync(
            _context.Database.CurrentTransaction!.GetDbTransaction()
        );
        var key = new OrganizationActivationKey
        {
            Id = "audit_failure_key_" + Guid.NewGuid().ToString("N"),
            KeyHash = OrganizationActivationKeyMaterial.ComputeHash(
                OrganizationActivationKeyMaterial.GenerateKey()
            ),
            CreatedByUserId = seed.Owner.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
        };
        var writer = new PostgresAuditTrailWriter(db, new AuditActorContext());
        if (operation == "revoke")
            await new PostgresOrganizationActivationKeyStore(db, writer).CreateKeyAsync(
                key,
                default
            );
        var callerOrganization = await db.Organizations.SingleAsync(row =>
            row.Id == seed.Organization.Id
        );
        var originalActive = callerOrganization.ActivatedAtUtc;
        callerOrganization.DisplayName = "pending security caller name";
        if (operation == "pretracked-create")
            db.OrganizationActivationKeys.Add(key);
        var failingWriter = failAfterSave
            ? (IAuditTrailWriter)writer
            : new AppendThenThrowWriter(writer);
        interceptor.Enabled = failAfterSave;
        await Assert
            .That(async () =>
            {
                if (operation is "create" or "pretracked-create")
                    await new PostgresOrganizationActivationKeyStore(
                        db,
                        failingWriter
                    ).CreateKeyAsync(key, default);
                else if (operation == "revoke")
                    await new PostgresOrganizationActivationKeyStore(
                        db,
                        failingWriter
                    ).RevokeKeyAsync(key.Id, default);
                else
                    await new PostgresSystemOrganizationManagementStore(
                        db,
                        failingWriter
                    ).UpdateOrganizationAdminStateAsync(
                        seed.Organization.Id,
                        false,
                        null,
                        null,
                        default
                    );
            })
            .Throws<InvalidOperationException>();
        interceptor.Enabled = false;
        await db.SaveChangesAsync();
        var savedOrganization = await _context
            .Organizations.AsNoTracking()
            .SingleAsync(row => row.Id == seed.Organization.Id);
        await Assert.That(savedOrganization.DisplayName).IsEqualTo("pending security caller name");
        await Assert.That(savedOrganization.ActivatedAtUtc).IsEqualTo(originalActive);
        await Assert.That(savedOrganization.DisabledAtUtc).IsNull();
        await Assert.That(await _context.OrganizationAuditEvents.CountAsync()).IsEqualTo(0);
        await Assert
            .That(await _context.SystemAuditEvents.CountAsync())
            .IsEqualTo(operation == "revoke" ? 1 : 0);
        if (operation == "revoke")
            await Assert
                .That(
                    (
                        await _context
                            .OrganizationActivationKeys.AsNoTracking()
                            .SingleAsync(row => row.Id == key.Id)
                    ).DisabledAtUtc
                )
                .IsNull();
        else
            await Assert
                .That(await _context.OrganizationActivationKeys.AnyAsync(row => row.Id == key.Id))
                .IsFalse();
    }

    [Test]
    public async Task FailedSave_RestoresUnrelatedAddedAndDeletedCallerEntries()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var interceptor = new FailAfterSave();
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                _context.Database.GetDbConnection(),
                npgsql =>
                {
                    npgsql.SetPostgresVersion(18, 0);
                    npgsql.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new PostgresDbContext(options);
        await db.Database.UseTransactionAsync(
            _context.Database.CurrentTransaction!.GetDbTransaction()
        );
        var deleted = new OrganizationActivationKey
        {
            Id = "caller_deleted_key_" + Guid.NewGuid().ToString("N"),
            KeyHash = OrganizationActivationKeyMaterial.ComputeHash(
                OrganizationActivationKeyMaterial.GenerateKey()
            ),
            CreatedByUserId = seed.Owner.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
        };
        db.OrganizationActivationKeys.Add(deleted);
        await db.SaveChangesAsync();
        db.OrganizationActivationKeys.Remove(deleted);
        var added = new Zeeq.Core.Documents.Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "pending-caller-library",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(added);
        interceptor.Enabled = true;
        await Assert
            .That(async () =>
                await new PostgresZeeqMembershipStore(db).UpdateMemberRoleAsync(
                    seed.Organization.Id,
                    seed.Owner.Id,
                    "admin",
                    default
                )
            )
            .Throws<InvalidOperationException>();
        await Assert.That(db.Entry(added).State).IsEqualTo(EntityState.Added);
        await Assert.That(db.Entry(deleted).State).IsEqualTo(EntityState.Deleted);
        interceptor.Enabled = false;
        await db.SaveChangesAsync();
        await Assert
            .That(await _context.Libraries.AsNoTracking().AnyAsync(row => row.Id == added.Id))
            .IsTrue();
        await Assert
            .That(
                await _context
                    .OrganizationActivationKeys.AsNoTracking()
                    .AnyAsync(row => row.Id == deleted.Id)
            )
            .IsFalse();
        await Assert.That(await _context.OrganizationAuditEvents.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task FailedOwnedMutation_ContextReuseDoesNotReplayFactOrChange()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException(
                "Owned transaction test requires its own test container."
            );
        await using var isolated = new PgDatabaseFixture();
        await isolated.InitializeAsync();
        await using var db = isolated.CreateContext();
        await TestBiome.Start(db, out var seed).BuildAsync();
        var originalRole = await db
            .OrganizationMemberships.AsNoTracking()
            .Where(row => row.OrganizationId == seed.Organization.Id && row.UserId == seed.Owner.Id)
            .Select(row => row.Role)
            .SingleAsync();
        seed.Organization.DisplayName = "pending owned caller name";
        var store = new PostgresZeeqMembershipStore(
            db,
            new AppendThenThrowWriter(new PostgresAuditTrailWriter(db, new AuditActorContext()))
        );
        await Assert
            .That(async () =>
                await store.UpdateMemberRoleAsync(
                    seed.Organization.Id,
                    seed.Owner.Id,
                    "admin",
                    default
                )
            )
            .Throws<InvalidOperationException>();
        await db.SaveChangesAsync();
        await using var read = isolated.CreateContext();
        await Assert
            .That(
                await read
                    .OrganizationMemberships.Where(row =>
                        row.OrganizationId == seed.Organization.Id && row.UserId == seed.Owner.Id
                    )
                    .Select(row => row.Role)
                    .SingleAsync()
            )
            .IsEqualTo(originalRole);
        await Assert.That(await read.OrganizationAuditEvents.CountAsync()).IsEqualTo(0);
        await Assert
            .That(
                (
                    await read.Organizations.SingleAsync(row => row.Id == seed.Organization.Id)
                ).DisplayName
            )
            .IsEqualTo("pending owned caller name");
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        ) =>
            Enabled
                ? throw new InvalidOperationException(
                    "Injected failure after SQL and tracker acceptance."
                )
                : ValueTask.FromResult(result);
    }

    private sealed class AppendThenThrowWriter(IAuditTrailWriter inner) : IAuditTrailWriter
    {
        public void AppendSystem(AuditChange change)
        {
            inner.AppendSystem(change);
            throw new InvalidOperationException("Injected failure after append.");
        }

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
        {
            inner.AppendOrganization(organizationId, teamId, change);
            throw new InvalidOperationException("Injected failure after append.");
        }
    }

    private sealed class FailingOrganizationWriter(IAuditTrailWriter inner) : IAuditTrailWriter
    {
        public void AppendSystem(AuditChange change) => inner.AppendSystem(change);

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change) =>
            throw new InvalidOperationException("Injected tenant audit failure.");
    }

    private IAuditTrailWriter Writer() =>
        new PostgresAuditTrailWriter(
            _context,
            new AuditActorContext
            {
                Current = new("user", "auditing_user", "Audit User", "web", "auditing_user"),
            }
        );

    private static AuditSnapshot Snapshot(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<AuditSnapshot>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        )!;
}
