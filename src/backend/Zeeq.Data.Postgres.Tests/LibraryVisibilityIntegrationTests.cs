using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class LibraryVisibilityIntegrationTests(PgDatabaseFixture postgres)
{
    [Test]
    public async Task Visibility_UsesCurrentMembershipAndFailsClosedWhenTheOnlyTeamIsDisabled()
    {
        await using var db = postgres.CreateContext();
        await TestBiome
            .Start(db, out _)
            .AddOrganization(out var organization)
            .AddOrganization(out var other)
            .BuildAsync();
        var ct = CancellationToken.None;
        var orgId = organization.Organization.Id;
        var memberId = organization.Organization.CreatedByUserId;
        var teams = new PostgresTeamManagementStore(db);
        var engineering = (CreateTeamResult.Saved)
            await teams.CreateAsync(orgId, memberId, "Engineering", null, [memberId], ct);
        var platform = (CreateTeamResult.Saved)
            await teams.CreateAsync(orgId, memberId, "Platform", null, [memberId], ct);
        var documents = new PostgresLibraryDocumentStore(db, new DocumentSearchScope());
        var actor = new AuditActorContext
        {
            Current = new AuditActor(
                AuditActorKinds.User,
                memberId,
                "Library administrator",
                AuditChannels.Web
            ),
        };
        var visibility = new PostgresLibraryVisibilityStore(
            db,
            documents,
            new PostgresAuditTrailWriter(db, actor)
        );
        var library = await documents.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = orgId,
                Name = "handbook",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            ct
        );

        var member = new LibraryAudience(orgId, memberId, false);
        var support = new LibraryAudience(orgId, "user-support", false);
        var admin = new LibraryAudience(orgId, "user-admin", true);
        await Assert.That(await visibility.CanOpenAsync(support, library.Name, ct)).IsTrue();

        var save = await visibility.SaveAsync(
            library,
            new([engineering.TeamId, platform.TeamId], [], false),
            false,
            ct
        );
        await Assert.That(save.Status).IsEqualTo(LibraryVisibilitySaveStatus.Saved);
        var settings = await visibility.GetSettingsAsync(orgId, library.Name, ct);
        await Assert.That(settings!.TeamIds).IsEquivalentTo([engineering.TeamId, platform.TeamId]);
        await Assert
            .That(settings.Teams.Select(team => team.Name))
            .IsEquivalentTo(["Engineering", "Platform"]);
        await Assert.That(await visibility.CanOpenAsync(support, library.Name, ct)).IsFalse();
        await Assert.That(await visibility.ListVisibleAsync(support, ct)).IsEmpty();
        await Assert.That(await visibility.CanOpenAsync(member, library.Name, ct)).IsTrue();
        await Assert.That(await visibility.CanOpenAsync(admin, library.Name, ct)).IsTrue();
        await Assert
            .That(
                await visibility.CanOpenAsync(
                    admin with
                    {
                        OrganizationId = other.Organization.Id,
                    },
                    library.Name,
                    ct
                )
            )
            .IsFalse();
        await Assert
            .That((await documents.ListLibrariesAsync(orgId, ct)).Select(row => row.Id))
            .Contains(library.Id);

        var stale = await visibility.SaveAsync(library, new([], [], true), false, ct);
        await Assert.That(stale.Status).IsEqualTo(LibraryVisibilitySaveStatus.Conflict);
        var forbidden = await visibility.SaveAsync(
            library,
            new([organization.RootTeam.Id], settings.TeamIds, false),
            false,
            ct
        );
        await Assert.That(forbidden.Status).IsEqualTo(LibraryVisibilitySaveStatus.InvalidTeams);

        var noChange = await visibility.SaveAsync(
            library,
            new([platform.TeamId, engineering.TeamId], settings.TeamIds, false),
            false,
            ct
        );
        await Assert.That(noChange.Status).IsEqualTo(LibraryVisibilitySaveStatus.Saved);

        var failing = new PostgresLibraryVisibilityStore(db, documents, new FailingAuditWriter());
        library.Description = "Uncommitted description";
        await Assert
            .That(async () =>
                await failing.SaveAsync(library, new([], settings.TeamIds, true), false, ct)
            )
            .Throws<InvalidOperationException>();
        db.ChangeTracker.Clear();
        library = (await documents.GetLibraryAsync(orgId, library.Name, ct))!;
        await Assert.That(library.Description).IsNull();
        await Assert.That(await visibility.CanOpenAsync(support, library.Name, ct)).IsFalse();

        await teams.UpdateAsync(orgId, engineering.TeamId, memberId, [], [memberId], ct);
        await Assert.That(await visibility.CanOpenAsync(member, library.Name, ct)).IsTrue();
        await teams.UpdateAsync(orgId, platform.TeamId, memberId, [], [memberId], ct);
        await Assert.That(await visibility.CanOpenAsync(member, library.Name, ct)).IsFalse();
        await teams.UpdateAsync(orgId, platform.TeamId, memberId, [memberId], [], ct);
        await db
            .Teams.Where(team => team.OrganizationId == orgId && team.Id == platform.TeamId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(team => team.DisabledAtUtc, DateTimeOffset.UtcNow),
                ct
            );

        await Assert.That(await visibility.CanOpenAsync(member, library.Name, ct)).IsFalse();
        var archived = await visibility.GetSettingsAsync(orgId, library.Name, ct);
        await Assert
            .That(archived!.Teams.Single(team => team.Id == platform.TeamId).IsAvailable)
            .IsFalse();
        var removal = await visibility.SaveAsync(
            library,
            new([platform.TeamId], archived.TeamIds, false),
            false,
            ct
        );
        await Assert.That(removal.Status).IsEqualTo(LibraryVisibilitySaveStatus.Saved);
        await Assert.That(await visibility.CanOpenAsync(support, library.Name, ct)).IsFalse();

        var unconfirmed = await visibility.SaveAsync(
            library,
            new([], [platform.TeamId], false),
            false,
            ct
        );
        await Assert
            .That(unconfirmed.Status)
            .IsEqualTo(LibraryVisibilitySaveStatus.ConfirmationRequired);
        await Assert.That(await visibility.CanOpenAsync(support, library.Name, ct)).IsFalse();
        var broadened = await visibility.SaveAsync(
            library,
            new([], [platform.TeamId], true),
            false,
            ct
        );
        await Assert.That(broadened.Status).IsEqualTo(LibraryVisibilitySaveStatus.Saved);
        await Assert.That(await visibility.CanOpenAsync(support, library.Name, ct)).IsTrue();

        var changes = await db
            .OrganizationAuditEvents.AsNoTracking()
            .Where(row =>
                row.OrganizationId == orgId
                && row.LibraryId == library.Id
                && row.EventType == "library.visibility_changed"
            )
            .OrderBy(row => row.OccurredAtUtc)
            .ThenBy(row => row.Id)
            .ToArrayAsync(ct);
        await Assert.That(changes.Length).IsEqualTo(3);
        await Assert
            .That(changes.Select(row => row.ActorId))
            .IsEquivalentTo([memberId, memberId, memberId]);
        await Assert
            .That(changes.Select(row => row.OperationId).Distinct().Single())
            .IsEqualTo(actor.OperationId);
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(changes[0].BeforeSnapshot)!.Fields["visibility"]
            )
            .IsEqualTo("[]");
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(changes[0].AfterSnapshot)!.Fields["visibility"]
            )
            .IsEqualTo("[\"Engineering\",\"Platform\"]");
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(changes[1].BeforeSnapshot)!.Fields["visibility"]
            )
            .IsEqualTo("[\"Engineering\",\"Platform (archived)\"]");
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(changes[1].AfterSnapshot)!.Fields["visibility"]
            )
            .IsEqualTo("[\"Platform (archived)\"]");
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(changes[2].BeforeSnapshot)!.Fields[
                    "visibilityTeamIds"
                ]
            )
            .IsEqualTo(System.Text.Json.JsonSerializer.Serialize(new[] { platform.TeamId }));
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(changes[2].AfterSnapshot)!.Fields["visibility"]
            )
            .IsEqualTo("[]");
    }

    private sealed class FailingAuditWriter : IAuditTrailWriter
    {
        public void AppendOrganization(string organizationId, string? teamId, AuditChange change) =>
            throw new InvalidOperationException("Simulated audit write failure.");

        public void AppendSystem(AuditChange change) => throw new NotSupportedException();
    }
}
