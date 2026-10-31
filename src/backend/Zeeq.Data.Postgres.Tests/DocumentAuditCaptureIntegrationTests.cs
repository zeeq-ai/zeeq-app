using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>Audit capture for document writes and synchronized document facts.
/// Run: dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --no-restore -- --treenode-filter "/*/*/DocumentAuditCaptureIntegrationTests/*"</summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DocumentAuditCaptureIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task TrackedEdit_RecordsDatabasePreimageAndPersistsRequestedValues()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/one.md", "old body", "old-hash");
        await store.UpsertDocumentAsync(document, default);
        document.Content = "new body";
        document.ContentHash = "new-hash";
        document.Title = "New title";
        await store.UpsertDocumentAsync(document, default);
        await store.UpsertDocumentAsync(document, default);
        _context.ChangeTracker.Clear();

        var saved = await _context.LibraryDocuments.SingleAsync(d => d.Id == document.Id);
        var events = await EventsAsync(org);
        var update = events.Single(e => e.EventType == "document.updated");
        await Assert.That(events.Length).IsEqualTo(2);
        await Assert.That(saved.Content).IsEqualTo("new body");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(update.BeforeSnapshot)!.Content)
            .IsEqualTo("old body");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(update.AfterSnapshot)!.Content)
            .IsEqualTo("new body");
        await Assert.That(update.BeforeHash).IsEqualTo("old-hash");
        await Assert.That(update.AfterHash).IsEqualTo("new-hash");
    }

    [Test]
    public async Task RenameSettingsAndDelete_RecordOneFactPerChange()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/one.md", "body", "hash");
        await store.UpsertDocumentAsync(document, default);
        await store.MoveDocumentAsync(org, library, "/one.md", "/two.md", default);
        await store.MoveDocumentAsync(org, library, "/one.md", "/two.md", default);
        await store.SetCodeReviewExclusionAsync(org, library, document.Id, true, default);
        await store.SetCodeReviewExclusionAsync(org, library, document.Id, true, default);
        await store.SetScopedSkillAsync(
            org,
            library,
            document.Id,
            LibraryDocumentScopedSkill.Library,
            default
        );
        await store.SetScopedSkillAsync(
            org,
            library,
            document.Id,
            LibraryDocumentScopedSkill.Library,
            default
        );
        _context.ChangeTracker.Clear();
        var moved = await _context.LibraryDocuments.SingleAsync(d => d.Id == document.Id);
        await Assert.That(moved.PreviousPaths).Contains("/one.md");
        await store.DeleteDocumentAsync(org, library, "/two.md", default);
        var events = await EventsAsync(org);
        await Assert
            .That(events.Select(e => e.EventType))
            .IsEquivalentTo(
                new[]
                {
                    "document.created",
                    "document.renamed",
                    "document.review_exclusion_changed",
                    "document.skill_scope_changed",
                    "document.deleted",
                }
            );
        var deleted = events.Single(e => e.EventType == "document.deleted");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(deleted.BeforeSnapshot)!.Content)
            .IsEqualTo("body");
        await Assert.That(deleted.AfterSnapshot).IsNull();
    }

    [Test]
    public async Task SyncChanges_RecordFactsWithoutContentAndSuppressRestamps()
    {
        var (store, org, library) = await SetupAsync();
        var first = NewDocument(org, library, "/one.md", "private synced text", "hash1");
        first.SyncRunId = "run1";
        await store.UpsertSyncedDocumentAsync(first, default);
        var edit = NewDocument(org, library, "/one.md", "different synced text", "hash2");
        edit.SyncRunId = "run2";
        await store.UpsertSyncedDocumentAsync(edit, default);
        var move = NewDocument(org, library, "/two.md", "different synced text", "hash2");
        move.SyncRunId = "run3";
        await store.UpsertSyncedDocumentAsync(move, default);
        var repeat = NewDocument(org, library, "/two.md", "different synced text", "hash2");
        repeat.SyncRunId = "run4";
        await store.UpsertSyncedDocumentAsync(repeat, default);
        await store.DeleteUnstampedAsync(org, library, "run5", default);
        var events = await EventsAsync(org);
        await Assert.That(events.Length).IsEqualTo(4);
        await Assert
            .That(events.Select(e => e.ChangeType))
            .IsEquivalentTo(new[] { "Added", "Edited", "Edited", "Deleted" });
        foreach (var audit in events)
        {
            await Assert.That(audit.EventType).IsEqualTo("document.synced");
            await Assert.That(audit.ActorChannel).IsEqualTo("worker");
            var before = AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot);
            var after = AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot);
            await Assert.That(before?.Content).IsNull();
            await Assert.That(after?.Content).IsNull();
            await Assert.That(before?.Title).IsNull();
            await Assert.That(after?.Title).IsNull();
        }
        await Assert
            .That(events.Single(e => e.ChangeType == "Deleted").SourceReference)
            .IsEqualTo("sync-run:run5");
    }

    [Test]
    public async Task ManualChangesToSyncedDocument_DoNotCaptureSyncedContent()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/synced.md", "private synced content", "hash1");
        document.SyncRunId = "run1";
        await store.UpsertSyncedDocumentAsync(document, default);
        var edit = NewDocument(org, library, "/synced.md", "edited private content", "hash2");
        await store.UpsertDocumentAsync(edit, default);
        await store.SetCodeReviewExclusionAsync(org, library, document.Id, true, default);
        await store.SetScopedSkillAsync(
            org,
            library,
            document.Id,
            LibraryDocumentScopedSkill.Library,
            default
        );
        await store.MoveDocumentAsync(org, library, "/synced.md", "/moved.md", default);
        await store.DeleteDocumentAsync(org, library, "/moved.md", default);
        var events = await EventsAsync(org);
        await Assert.That(events.Length).IsEqualTo(6);
        foreach (var audit in events)
        {
            await Assert
                .That(AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot)?.Content)
                .IsNull();
            await Assert
                .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)?.Content)
                .IsNull();
            await Assert
                .That(AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot)?.Title)
                .IsNull();
            await Assert
                .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)?.Title)
                .IsNull();
        }
    }

    [Test]
    public async Task MetadataOnlyUpsert_PersistsChangesWithoutContentEvent()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/metadata.md", "same body", "hash1");
        await store.UpsertDocumentAsync(document, default);
        var edit = NewDocument(org, library, "/metadata.md", "same body", "hash2");
        edit.ParsedSkillName = "skill";
        edit.Keywords = ["updated"];
        edit.TokenCount = 123;
        edit.ProcessingStatus = DocumentProcessingStatus.Pending;
        await store.UpsertDocumentAsync(edit, default);
        _context.ChangeTracker.Clear();
        var saved = await _context.LibraryDocuments.SingleAsync(d => d.Id == document.Id);
        await Assert.That(saved.ContentHash).IsEqualTo("hash2");
        await Assert.That(saved.ParsedSkillName).IsEqualTo("skill");
        await Assert.That(saved.Keywords).IsEquivalentTo(new[] { "updated" });
        await Assert.That(saved.TokenCount).IsEqualTo(123);
        await Assert.That((await EventsAsync(org)).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Sweep_MultipleBatches_DeletesAndRecordsEveryDocument()
    {
        var (_, org, library) = await SetupAsync();
        await SeedSweepDocumentsAsync(org, library);
        new PostgresAuditTrailWriter(_context, new AuditActorContext()).AppendOrganization(
            org,
            null,
            new AuditChange
            {
                EventType = "caller.change",
                ChangeType = AuditChangeType.Edited,
                Area = AuditArea.Settings,
                SubjectType = "library",
                SubjectId = library,
                SubjectLabel = "Caller work before sweep",
            }
        );
        var callerFact = _context.OrganizationAuditEvents.Local.Single();
        var writer = new CountingWriter(_context);
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope(), writer);
        var deleted = await store.DeleteUnstampedAsync(org, library, "run-current", default);
        await Assert.That(deleted).IsEqualTo(405);
        var facts = await EventsAsync(org);
        await Assert.That(facts.Length).IsEqualTo(406);
        await Assert.That(facts.Count(fact => fact.EventType == "caller.change")).IsEqualTo(1);
        await Assert.That(_context.Entry(callerFact).State).IsEqualTo(EntityState.Unchanged);
        await Assert.That(writer.MaxPending).IsLessThanOrEqualTo(200);
        await Assert
            .That(await _context.LibraryDocuments.CountAsync(d => d.LibraryId == library))
            .IsEqualTo(0);
    }

    [Test]
    public async Task Sweep_AmbientPendingChildUpdate_LocksParentBeforeFlush()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException("Test requires its own test container.");
        await using var isolated = new PgDatabaseFixture();
        await isolated.InitializeAsync();
        await using var seedDb = isolated.CreateContext();
        await TestBiome.Start(seedDb, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var document = NewDocument(seed.Organization.Id, library.Id, "/pending.md", "body", "hash");
        document.SyncRunId = "current";
        seedDb.Libraries.Add(library);
        seedDb.LibraryDocuments.Add(document);
        await seedDb.SaveChangesAsync();
        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                isolated.ConnectionString,
                n =>
                {
                    n.SetPostgresVersion(18, 0);
                    n.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(recorder)
            .Options;
        await using var db = new PostgresDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync();
        var tracked = await db.LibraryDocuments.SingleAsync(d => d.Id == document.Id);
        tracked.Title = "pending change";
        recorder.Commands.Clear();
        await new PostgresLibraryDocumentStore(db, new DocumentSearchScope()).DeleteUnstampedAsync(
            seed.Organization.Id,
            library.Id,
            "current",
            default
        );
        var parent = recorder.Commands.FindIndex(s =>
            s.Contains("docs_libraries", StringComparison.OrdinalIgnoreCase)
            && s.Contains("FOR KEY SHARE", StringComparison.OrdinalIgnoreCase)
        );
        var childUpdate = recorder.Commands.FindIndex(s =>
            s.Contains("UPDATE zeeq.docs_library_documents", StringComparison.OrdinalIgnoreCase)
        );
        await Assert.That(parent >= 0 && childUpdate > parent).IsTrue();
        await tx.RollbackAsync();
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Test]
    public async Task Sweep_ConcurrentRestamp_WaitsAndDoesNotDeleteCurrentDocument()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException(
                "Concurrency test requires its own test container."
            );
        await using var isolated = new PgDatabaseFixture();
        await isolated.InitializeAsync();
        await using var seedDb = isolated.CreateContext();
        await TestBiome.Start(seedDb, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "concurrent",
            CreatedAt = now,
            UpdatedAt = now,
        };
        seedDb.Libraries.Add(library);
        var document = NewDocument(
            seed.Organization.Id,
            library.Id,
            "/concurrent.md",
            "body",
            "hash"
        );
        document.SyncRunId = "old-run";
        seedDb.LibraryDocuments.Add(document);
        await seedDb.SaveChangesAsync();

        await using var restampDb = isolated.CreateContext();
        await using var sweepDb = isolated.CreateContext();
        await using var transaction = await restampDb.Database.BeginTransactionAsync();
        await restampDb
            .LibraryDocuments.Where(row => row.Id == document.Id)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(row => row.SyncRunId, "current-run")
            );
        var store = new PostgresLibraryDocumentStore(sweepDb, new DocumentSearchScope());
        var sweep = store.DeleteUnstampedAsync(
            seed.Organization.Id,
            library.Id,
            "current-run",
            default
        );
        var completed = await Task.WhenAny(sweep, Task.Delay(200));
        await Assert.That(completed == sweep).IsFalse();
        await transaction.CommitAsync();
        await Assert.That(await sweep).IsEqualTo(0);
        await Assert
            .That(await sweepDb.LibraryDocuments.CountAsync(row => row.Id == document.Id))
            .IsEqualTo(1);
        await Assert
            .That(
                await sweepDb.OrganizationAuditEvents.CountAsync(row =>
                    row.OrganizationId == seed.Organization.Id
                )
            )
            .IsEqualTo(0);
    }

    [Test]
    public async Task Sweep_SecondBatchFailure_RollsBackEarlierDeletesAndFactsWithCallerTransaction()
    {
        var (_, org, library) = await SetupAsync();
        await SeedSweepDocumentsAsync(org, library);
        var now = DateTimeOffset.UtcNow;
        var callerLibrary = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = org,
            Name = "caller-work",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(callerLibrary);
        var store = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new CountingWriter(_context, failAt: 202)
        );
        await Assert
            .That(async () =>
                await store.DeleteUnstampedAsync(org, library, "run-current", default)
            )
            .Throws<InvalidOperationException>();
        // The caller catches the error and continues without rolling back its transaction.
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await Assert
            .That(await _context.LibraryDocuments.CountAsync(d => d.LibraryId == library))
            .IsEqualTo(405);
        await Assert.That(await EventsAsync(org)).IsEmpty();
        await Assert
            .That(await _context.Libraries.AnyAsync(row => row.Id == callerLibrary.Id))
            .IsTrue();
    }

    private async Task SeedSweepDocumentsAsync(string org, string library)
    {
        for (var i = 0; i < 405; i++)
        {
            var document = NewDocument(org, library, $"/sweep/{i}.md", "body", $"hash-{i}");
            document.SyncRunId = "old-run";
            _context.LibraryDocuments.Add(document);
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private sealed class CountingWriter(PostgresDbContext db, int failAt = int.MaxValue)
        : IAuditTrailWriter
    {
        private readonly PostgresAuditTrailWriter inner = new(db, new AuditActorContext());
        private int count;
        public int MaxPending { get; private set; }

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
        {
            if (++count == failAt)
                throw new InvalidOperationException("injected failure");
            inner.AppendOrganization(organizationId, teamId, change);
            MaxPending = Math.Max(
                MaxPending,
                db.ChangeTracker.Entries<OrganizationAuditEvent>()
                    .Count(entry => entry.State == EntityState.Added)
            );
        }

        public void AppendSystem(AuditChange change) => inner.AppendSystem(change);
    }

    [Test]
    public async Task SyncRun_UsesStableOperationAcrossWriterScopesAndSweep()
    {
        var (_, org, library) = await SetupAsync();
        var firstRun = $"run_{Guid.CreateVersion7():N}";
        var secondRun = $"run_{Guid.CreateVersion7():N}";
        var firstStore = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new PostgresAuditTrailWriter(_context, new AuditActorContext())
        );
        var secondStore = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new PostgresAuditTrailWriter(_context, new AuditActorContext())
        );
        var first = NewDocument(org, library, "/one.md", "private one", "hash1");
        first.SyncRunId = firstRun;
        var second = NewDocument(org, library, "/two.md", "private two", "hash2");
        second.SyncRunId = firstRun;
        var third = NewDocument(org, library, "/three.md", "private three", "hash3");
        third.SyncRunId = secondRun;
        await firstStore.UpsertSyncedDocumentAsync(first, default);
        await secondStore.UpsertSyncedDocumentAsync(second, default);
        await secondStore.UpsertSyncedDocumentAsync(third, default);
        await secondStore.DeleteUnstampedAsync(org, library, secondRun, default);

        var events = await EventsAsync(org);
        var firstEvents = events.Where(e => e.SourceReference == $"sync-run:{firstRun}").ToArray();
        var secondEvents = events
            .Where(e => e.SourceReference == $"sync-run:{secondRun}")
            .ToArray();
        await Assert.That(firstEvents.Length).IsEqualTo(2);
        await Assert.That(secondEvents.Length).IsEqualTo(3);
        foreach (var audit in firstEvents)
            await Assert.That(audit.OperationId).IsEqualTo(Guid.ParseExact(firstRun[4..], "N"));
        foreach (var audit in secondEvents)
            await Assert.That(audit.OperationId).IsEqualTo(Guid.ParseExact(secondRun[4..], "N"));
        foreach (var audit in events)
        {
            await Assert
                .That(AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot)?.Content)
                .IsNull();
            await Assert
                .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)?.Content)
                .IsNull();
        }
    }

    [Test]
    public async Task ExternalSyncDelete_RecordsContentFreeFact()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/notion.md", "private notion body", "hash");
        document.SourceExternalId = "page1";
        document.SyncRunId = "run1";
        await store.UpsertSyncedDocumentAsync(document, default);
        await store.DeleteDocumentByExternalIdAsync(org, library, "page1", default);
        var deleted = (await EventsAsync(org)).Single(e => e.ChangeType == "Deleted");
        var before = AuditSnapshotSerializer.Deserialize(deleted.BeforeSnapshot)!;
        await Assert.That(before.Content).IsNull();
        await Assert.That(before.Fields["externalId"]).IsEqualTo("page1");
        await Assert.That(deleted.SourceReference).IsEqualTo("external-id:page1");
    }

    [Test]
    public async Task FailedAuditAppend_DoesNotSaveDocumentEdit()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/one.md", "old body", "old-hash");
        await store.UpsertDocumentAsync(document, default);
        var failingStore = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new ThrowingWriter()
        );
        var edit = NewDocument(org, library, "/one.md", "new body", "new-hash");
        await Assert
            .That(async () => await failingStore.UpsertDocumentAsync(edit, default))
            .Throws<InvalidOperationException>();
        _context.ChangeTracker.Clear();
        var saved = await _context.LibraryDocuments.SingleAsync(d => d.Id == document.Id);
        await Assert.That(saved.Content).IsEqualTo("old body");
        await Assert.That((await EventsAsync(org)).Length).IsEqualTo(1);
    }

    [Test]
    public async Task FailedAppend_TrackedAndNewDocuments_DoNotReplayWhenCallerContinues()
    {
        var (store, org, library) = await SetupAsync();
        var existing = NewDocument(org, library, "/existing.md", "old", "old-hash");
        await store.UpsertDocumentAsync(existing, default);
        var before = (await EventsAsync(org)).Length;
        var failing = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new AppendThenThrowWriter(_context)
        );
        existing.Content = "rejected";
        existing.ContentHash = "rejected-hash";
        await Assert
            .That(async () => await failing.UpsertDocumentAsync(existing, default))
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        await Assert
            .That(
                (
                    await _context
                        .LibraryDocuments.AsNoTracking()
                        .SingleAsync(d => d.Id == existing.Id)
                ).Content
            )
            .IsEqualTo("old");
        var fresh = NewDocument(org, library, "/new.md", "new", "new-hash");
        await Assert
            .That(async () => await failing.UpsertDocumentAsync(fresh, default))
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        await Assert
            .That(await _context.LibraryDocuments.AsNoTracking().AnyAsync(d => d.Id == fresh.Id))
            .IsFalse();
        await Assert.That((await EventsAsync(org)).Length).IsEqualTo(before);
    }

    [Test]
    public async Task FailedDelete_PreservesPreexistingTrackedEditAndDoesNotDeleteOrRecordFact()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/pending-delete.md", "old", "old-hash");
        await store.UpsertDocumentAsync(document, default);
        var before = (await EventsAsync(org)).Length;
        document.Content = "caller pending edit";
        var failing = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new AppendThenThrowWriter(_context)
        );
        await Assert
            .That(async () =>
                await failing.DeleteDocumentAsync(org, library, document.Path, default)
            )
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        var saved = await _context
            .LibraryDocuments.AsNoTracking()
            .SingleAsync(d => d.Id == document.Id);
        await Assert.That(saved.Content).IsEqualTo("caller pending edit");
        await Assert.That((await EventsAsync(org)).Length).IsEqualTo(before);
    }

    [Test]
    public async Task FailedSyncedAppend_DoesNotReplayTrackedDocument()
    {
        var (store, org, library) = await SetupAsync();
        var existing = NewDocument(org, library, "/sync.md", "old", "old-hash");
        existing.SyncRunId = "old";
        await store.UpsertSyncedDocumentAsync(existing, default);
        var before = (await EventsAsync(org)).Length;
        var request = NewDocument(org, library, "/sync.md", "rejected", "new-hash");
        request.SyncRunId = "new";
        var failing = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new AppendThenThrowWriter(_context)
        );
        await Assert
            .That(async () => await failing.UpsertSyncedDocumentAsync(request, default))
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        var saved = await _context
            .LibraryDocuments.AsNoTracking()
            .SingleAsync(d => d.Id == existing.Id);
        await Assert.That(saved.Content).IsEqualTo("old");
        await Assert.That(saved.SyncRunId).IsEqualTo("old");
        await Assert.That((await EventsAsync(org)).Length).IsEqualTo(before);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task FailureAfterSave_RollsBackRawToggleAndKeepsCallerPendingWork(
        bool toggle,
        bool cancel
    )
    {
        var (setup, org, library) = await SetupAsync();
        var doc = NewDocument(org, library, "/save-failure.md", "old", "old-hash");
        await setup.UpsertDocumentAsync(doc, default);
        var prior = (await EventsAsync(org)).Length;
        var now = DateTimeOffset.UtcNow;
        var removed = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = org,
            Name = "remove caller",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(removed);
        await _context.SaveChangesAsync();
        var added = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = org,
            Name = "add caller",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var interceptor = new ThrowAfterSave();
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                _context.Database.GetDbConnection(),
                n =>
                {
                    n.SetPostgresVersion(18, 0);
                    n.UseVector();
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
        var tracked = await db.LibraryDocuments.SingleAsync(d => d.Id == doc.Id);
        var callerLibrary = await db.Libraries.SingleAsync(l => l.Id == library);
        callerLibrary.Description = "pending caller work";
        db.Libraries.Add(added);
        db.Libraries.Remove(await db.Libraries.SingleAsync(l => l.Id == removed.Id));
        var store = new PostgresLibraryDocumentStore(db, new DocumentSearchScope());
        using var cancellation = new CancellationTokenSource();
        interceptor.Cancel = cancel ? cancellation : null;
        interceptor.Enabled = true;
        if (cancel)
        {
            tracked.Content = "rejected";
            tracked.ContentHash = "rejected-hash";
            await Assert
                .That(async () => await store.UpsertDocumentAsync(tracked, cancellation.Token))
                .Throws<OperationCanceledException>();
        }
        else if (toggle)
            await Assert
                .That(async () =>
                    await store.SetCodeReviewExclusionAsync(org, library, doc.Id, true, default)
                )
                .Throws<InvalidOperationException>();
        else
        {
            tracked.Content = "rejected";
            tracked.ContentHash = "rejected-hash";
            await Assert
                .That(async () => await store.UpsertDocumentAsync(tracked, default))
                .Throws<InvalidOperationException>();
        }
        interceptor.Enabled = false;
        await db.SaveChangesAsync();
        var saved = await _context.LibraryDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id);
        await Assert.That(saved.Content).IsEqualTo("old");
        await Assert.That(saved.ExcludedFromCodeReviews).IsFalse();
        await Assert.That((await EventsAsync(org)).Length).IsEqualTo(prior);
        await Assert
            .That(
                (
                    await _context.Libraries.AsNoTracking().SingleAsync(l => l.Id == library)
                ).Description
            )
            .IsEqualTo("pending caller work");
        await Assert
            .That(await _context.Libraries.AsNoTracking().AnyAsync(l => l.Id == added.Id))
            .IsTrue();
        await Assert
            .That(await _context.Libraries.AsNoTracking().AnyAsync(l => l.Id == removed.Id))
            .IsFalse();
    }

    private sealed class ThrowAfterSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public CancellationTokenSource? Cancel { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            if (Enabled && Cancel is not null)
                Cancel.Cancel();
            else if (Enabled)
                throw new InvalidOperationException("failure after save");
            return ValueTask.FromResult(result);
        }
    }

    [Test]
    public async Task CancelledBeforeLock_DoesNotReplayTrackedTarget()
    {
        var (store, org, library) = await SetupAsync();
        var document = NewDocument(org, library, "/cancel.md", "old", "old-hash");
        await store.UpsertDocumentAsync(document, default);
        document.Content = "cancelled edit";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert
            .That(async () => await store.UpsertDocumentAsync(document, cancellation.Token))
            .Throws<OperationCanceledException>();
        await _context.SaveChangesAsync();
        await Assert
            .That(
                (
                    await _context
                        .LibraryDocuments.AsNoTracking()
                        .SingleAsync(d => d.Id == document.Id)
                ).Content
            )
            .IsEqualTo("old");
    }

    [Test]
    public async Task OwnedTransaction_AppendFailure_ContextCanBeReused()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException("Test requires its own test container.");
        await using var isolated = new PgDatabaseFixture();
        await isolated.InitializeAsync();
        await using var db = isolated.CreateContext();
        await TestBiome.Start(db, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var document = NewDocument(seed.Organization.Id, library.Id, "/owned.md", "body", "hash");
        var store = new PostgresLibraryDocumentStore(
            db,
            new DocumentSearchScope(),
            new AppendThenThrowWriter(db)
        );
        await Assert
            .That(async () => await store.UpsertDocumentAsync(document, default))
            .Throws<InvalidOperationException>();
        await db.SaveChangesAsync();
        await Assert
            .That(await db.LibraryDocuments.AsNoTracking().AnyAsync(d => d.Id == document.Id))
            .IsFalse();
        await Assert
            .That(
                await db
                    .OrganizationAuditEvents.AsNoTracking()
                    .AnyAsync(e => e.SubjectId == document.Id)
            )
            .IsFalse();
    }

    private sealed class AppendThenThrowWriter(PostgresDbContext db) : IAuditTrailWriter
    {
        private readonly PostgresAuditTrailWriter inner = new(db, new AuditActorContext());

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
        {
            inner.AppendOrganization(organizationId, teamId, change);
            throw new InvalidOperationException("append failed after attaching fact");
        }

        public void AppendSystem(AuditChange change) => throw new NotSupportedException();
    }

    private sealed class ThrowingWriter : IAuditTrailWriter
    {
        public void AppendOrganization(string organizationId, string? teamId, AuditChange change) =>
            throw new InvalidOperationException("audit append failed");

        public void AppendSystem(AuditChange change) =>
            throw new InvalidOperationException("audit append failed");
    }

    private async Task<(
        PostgresLibraryDocumentStore Store,
        string Org,
        string Library
    )> SetupAsync()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var now = DateTimeOffset.UtcNow;
        var library = await store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = seed.Organization.Id,
                Name = "docs",
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
        return (store, seed.Organization.Id, library.Id);
    }

    private static LibraryDocument NewDocument(
        string org,
        string library,
        string path,
        string content,
        string hash
    )
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryDocument
        {
            Id = SeedContext.NewId("document"),
            OrganizationId = org,
            LibraryId = library,
            Path = path,
            Title = "Title",
            TitleNormalized = "title",
            Content = content,
            ContentHash = hash,
            Keywords = [],
            Headings = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private Task<OrganizationAuditEvent[]> EventsAsync(string org) =>
        _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e =>
                e.OrganizationId == org
                && (e.SubjectType == "document" || e.EventType == "caller.change")
            )
            .ToArrayAsync();
}
