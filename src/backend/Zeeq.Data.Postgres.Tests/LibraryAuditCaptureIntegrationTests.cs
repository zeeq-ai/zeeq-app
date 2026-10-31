using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class LibraryAuditCaptureIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task DeleteOtherLibrary_WhileAddingTargetLink_DoesNotRestoreRemovedLink()
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
        var otherId = SeedContext.NewId("library");
        await TestBiome
            .Start(seedDb, out var seed)
            .AddRepository(out var repo, p => p.LibraryIds = [otherId])
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var other = new Library
        {
            Id = otherId,
            OrganizationId = seed.Organization.Id,
            Name = "other",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var target = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "target",
            CreatedAt = now,
            UpdatedAt = now,
        };
        seedDb.Libraries.AddRange(other, target);
        await seedDb.SaveChangesAsync();
        await using var deleteDb = isolated.CreateContext();
        var gate = new SqlBarrier("code_review_repositories", "FOR UPDATE", pause: false);
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                isolated.ConnectionString,
                npgsql =>
                {
                    npgsql.SetPostgresVersion(18, 0);
                    npgsql.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(gate)
            .Options;
        await using var mappingDb = new PostgresDbContext(options);
        await using var tx = await deleteDb.Database.BeginTransactionAsync();
        await new PostgresLibraryDocumentStore(
            deleteDb,
            new DocumentSearchScope()
        ).DeleteLibraryAsync(seed.Organization.Id, other.Id, default);
        var mapping = new PostgresCodeRepositoryStore(mappingDb).ReplaceLibraryRepositoriesAsync(
            seed.Organization.Id,
            target.Id,
            [repo.Id],
            default
        );
        await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await tx.CommitAsync();
        await mapping.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = await seedDb
            .CodeRepositories.AsNoTracking()
            .SingleAsync(row => row.Id == repo.Id);
        await Assert.That(saved.LibraryIds).IsEquivalentTo(new[] { target.Id });
        var facts = await seedDb
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e =>
                e.SubjectId == repo.Id && e.EventType == "repository.library_mapping_changed"
            )
            .ToArrayAsync();
        await Assert.That(facts.Length).IsEqualTo(2);
        await Assert
            .That(facts.Select(e => e.LibraryId!))
            .IsEquivalentTo(new[] { other.Id, target.Id });
    }

    [Test]
    public async Task RepositoryMapping_UnresolvableOldLink_ReturnsConflictWithoutSilentRemoval()
    {
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repo, prototype => prototype.LibraryIds = ["missing-library"])
            .BuildAsync();
        var store = new PostgresCodeRepositoryStore(_context);
        await Assert
            .That(async () =>
                await store.ReplaceRepositoryLibrariesAsync(
                    seed.Organization.Id,
                    repo.Id,
                    [],
                    default
                )
            )
            .Throws<LibraryMappingConflictException>();
        _context.ChangeTracker.Clear();
        await Assert
            .That(
                (await _context.CodeRepositories.SingleAsync(row => row.Id == repo.Id)).LibraryIds
            )
            .Contains("missing-library");
        await Assert
            .That(await _context.OrganizationAuditEvents.AnyAsync(row => row.SubjectId == repo.Id))
            .IsFalse();
    }

    [Test]
    public async Task Delete_ManyRepositoryLinks_BatchesFactsAndRollsBackOnLateFailure()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "many-links",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        for (var i = 0; i < 205; i++)
            _context.CodeRepositories.Add(
                new CodeRepository
                {
                    Id = SeedContext.NewId("repo"),
                    OrganizationId = seed.Organization.Id,
                    Provider = "github",
                    OwnerQualifiedName = $"example/{i}",
                    DisplayName = $"repo {i}",
                    LibraryIds = [library.Id],
                    ReviewConfiguration = new CodeRepositoryReviewConfiguration
                    {
                        CheckRun = new CodeRepositoryReviewCheckRunConfiguration(),
                    },
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                }
            );
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var tracked = await _context.CodeRepositories.FirstAsync(row =>
            row.OrganizationId == seed.Organization.Id
        );
        var failing = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new ThrowAfterWriter(_context, 202)
        );
        await Assert
            .That(async () =>
                await failing.DeleteLibraryAsync(seed.Organization.Id, library.Id, default)
            )
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        await Assert
            .That(
                await _context.CodeRepositories.CountAsync(row =>
                    row.OrganizationId == seed.Organization.Id
                    && row.LibraryIds.Contains(library.Id)
                )
            )
            .IsEqualTo(205);
        await Assert
            .That(
                await _context.OrganizationAuditEvents.AnyAsync(row =>
                    row.OrganizationId == seed.Organization.Id
                )
            )
            .IsFalse();
        await Assert.That(tracked.LibraryIds).Contains(library.Id);
        var writer = new MaxPendingWriter(_context);
        await new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            writer
        ).DeleteLibraryAsync(seed.Organization.Id, library.Id, default);
        await Assert.That(writer.MaxPending).IsLessThanOrEqualTo(200);
        await Assert.That(tracked.LibraryIds).DoesNotContain(library.Id);
        await Assert
            .That(_context.Entry(tracked).Property(row => row.LibraryIds).IsModified)
            .IsFalse();
        var events = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(row => row.OrganizationId == seed.Organization.Id)
            .ToArrayAsync();
        await Assert
            .That(events.Count(row => row.EventType == "repository.library_mapping_changed"))
            .IsEqualTo(205);
        var parent = events.Single(row => row.EventType == "library.deleted");
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(parent.BeforeSnapshot)!.Fields[
                    "repositoryMappingCount"
                ]
            )
            .IsEqualTo("205");
    }

    private sealed class MaxPendingWriter(PostgresDbContext db) : IAuditTrailWriter
    {
        private readonly PostgresAuditTrailWriter inner = new(db, new AuditActorContext());
        public int MaxPending { get; private set; }

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
        {
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
    public async Task Update_CancelAfterSave_RollsBackFactAndChange_AndPreservesCallerWork()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException(
                "Cancellation test requires its own test container."
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
            Name = "before",
            CreatedAt = now,
            UpdatedAt = now,
        };
        seedDb.Libraries.Add(library);
        await seedDb.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelAfterSave(cancellation);
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                isolated.ConnectionString,
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
        await using var tx = await db.Database.BeginTransactionAsync();
        var tracked = await db.Libraries.SingleAsync(row => row.Id == library.Id);
        var earlier = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "caller-pending",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Libraries.Add(earlier);
        tracked.Name = "after";
        interceptor.Enabled = true;
        await Assert
            .That(async () =>
                await new PostgresLibraryDocumentStore(
                    db,
                    new DocumentSearchScope()
                ).TryUpdateLibraryAsync(tracked, false, cancellation.Token)
            )
            .Throws<OperationCanceledException>();
        interceptor.Enabled = false;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await Assert
            .That(
                (
                    await seedDb.Libraries.AsNoTracking().SingleAsync(row => row.Id == library.Id)
                ).Name
            )
            .IsEqualTo("before");
        await Assert
            .That(await seedDb.Libraries.AsNoTracking().AnyAsync(row => row.Id == earlier.Id))
            .IsTrue();
        await Assert
            .That(await seedDb.OrganizationAuditEvents.AnyAsync(row => row.SubjectId == library.Id))
            .IsFalse();
    }

    private sealed class CancelAfterSave(CancellationTokenSource cancellation)
        : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            if (Enabled)
                cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    [Test]
    public async Task Delete_WithPendingChildEdit_LocksParentBeforeFlushAndAvoidsDeadlock()
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
        var document = new LibraryDocument
        {
            Id = SeedContext.NewId("document"),
            OrganizationId = seed.Organization.Id,
            LibraryId = library.Id,
            Path = "/doc.md",
            Title = "Title",
            TitleNormalized = "title",
            Content = "old",
            ContentHash = "old-hash",
            Keywords = [],
            Headings = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        seedDb.Libraries.Add(library);
        seedDb.LibraryDocuments.Add(document);
        await seedDb.SaveChangesAsync();
        var deleteGate = new SqlBarrier("docs_libraries", "FOR UPDATE", pause: false);
        var editGate = new SqlBarrier("docs_library_documents", "FOR UPDATE", pause: true);
        PostgresDbContext MakeContext(DbCommandInterceptor interceptor) =>
            new(
                new DbContextOptionsBuilder<PostgresDbContext>()
                    .UseNpgsql(
                        isolated.ConnectionString,
                        npgsql =>
                        {
                            npgsql.SetPostgresVersion(18, 0);
                            npgsql.UseVector();
                        }
                    )
                    .UseOpenIddict()
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(interceptor)
                    .Options
            );
        await using var deleteDb = MakeContext(deleteGate);
        await using var editDb = MakeContext(editGate);
        await using var deletingTransaction = await deleteDb.Database.BeginTransactionAsync();
        var pending = await deleteDb.LibraryDocuments.SingleAsync(row => row.Id == document.Id);
        pending.TokenCount = 99;
        var edit = new LibraryDocument
        {
            Id = document.Id,
            OrganizationId = document.OrganizationId,
            LibraryId = library.Id,
            Path = document.Path,
            Title = document.Title,
            TitleNormalized = document.TitleNormalized,
            Content = "committed edit",
            ContentHash = "new-hash",
            Keywords = [],
            Headings = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        async Task ApplyEditAsync()
        {
            await using var editTransaction = await editDb.Database.BeginTransactionAsync();
            await new PostgresLibraryDocumentStore(
                editDb,
                new DocumentSearchScope()
            ).UpsertDocumentAsync(edit, default);
            await editTransaction.CommitAsync();
        }
        var editTask = ApplyEditAsync();
        await editGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deleteTask = new PostgresLibraryDocumentStore(
            deleteDb,
            new DocumentSearchScope()
        ).DeleteLibraryAsync(seed.Organization.Id, library.Id, default);
        await deleteGate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        editGate.Resume.TrySetResult();
        await editTask.WaitAsync(TimeSpan.FromSeconds(10));
        await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));
        var fact = await deleteDb
            .OrganizationAuditEvents.AsNoTracking()
            .SingleAsync(e => e.SubjectId == document.Id && e.EventType == "document.deleted");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(fact.BeforeSnapshot)!.Content)
            .IsEqualTo("committed edit");
        await deletingTransaction.CommitAsync();
        await Assert
            .That(await seedDb.Libraries.AsNoTracking().AnyAsync(row => row.Id == library.Id))
            .IsFalse();
        await Assert
            .That(
                await seedDb.LibraryDocuments.AsNoTracking().AnyAsync(row => row.Id == document.Id)
            )
            .IsFalse();
    }

    private sealed class SqlBarrier(string table, string clause, bool pause) : DbCommandInterceptor
    {
        private int stopped;
        public TaskCompletionSource Arrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                command.CommandText.Contains(table, StringComparison.Ordinal)
                && command.CommandText.Contains(clause, StringComparison.Ordinal)
                && Interlocked.Exchange(ref stopped, 1) == 0
            )
            {
                Arrived.SetResult();
                if (pause)
                    await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Test]
    public async Task RepositoryMapping_WhenLibraryAppearsBetweenLocks_RetriesAndAuditsRemoval()
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
        await TestBiome.Start(seedDb, out var seed).AddRepository(out var repo).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var first = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "first",
            CreatedAt = now,
            UpdatedAt = now,
        };
        seedDb.Libraries.Add(first);
        await seedDb.SaveChangesAsync();
        var gate = new BeforeRepositoryLockGate();
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(
                isolated.ConnectionString,
                npgsql =>
                {
                    npgsql.SetPostgresVersion(18, 0);
                    npgsql.UseVector();
                }
            )
            .UseOpenIddict()
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(gate)
            .Options;
        await using var waitingDb = new PostgresDbContext(options);
        await using var writerDb = isolated.CreateContext();
        var waiting = new PostgresCodeRepositoryStore(waitingDb).ReplaceRepositoryLibrariesAsync(
            seed.Organization.Id,
            repo.Id,
            [],
            default
        );
        await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var added = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "added",
            CreatedAt = now,
            UpdatedAt = now,
        };
        writerDb.Libraries.Add(added);
        await writerDb.SaveChangesAsync();
        await new PostgresCodeRepositoryStore(writerDb).ReplaceRepositoryLibrariesAsync(
            seed.Organization.Id,
            repo.Id,
            [added.Id],
            default
        );
        gate.Resume.SetResult();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        var facts = await seedDb
            .OrganizationAuditEvents.AsNoTracking()
            .Where(row =>
                row.SubjectId == repo.Id
                && row.LibraryId == added.Id
                && row.EventType == "repository.library_mapping_changed"
            )
            .ToArrayAsync();
        await Assert.That(facts.Length).IsEqualTo(2);
        await Assert
            .That(
                (
                    await seedDb
                        .CodeRepositories.AsNoTracking()
                        .SingleAsync(row => row.Id == repo.Id)
                ).LibraryIds
            )
            .IsEmpty();
    }

    private sealed class BeforeRepositoryLockGate : DbCommandInterceptor
    {
        private int stopped;
        public TaskCompletionSource Arrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                command.CommandText.Contains("code_review_repositories", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)
                && Interlocked.Exchange(ref stopped, 1) == 0
            )
            {
                Arrived.SetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Test]
    public async Task NewRepository_AuditFailure_RestoresDetachedInputLinks()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        var repository = new CodeRepository
        {
            Id = SeedContext.NewId("repo"),
            OrganizationId = seed.Organization.Id,
            Provider = "github",
            OwnerQualifiedName = "example/repo",
            DisplayName = "repo",
            LibraryIds = [library.Id],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var store = new PostgresCodeRepositoryStore(_context, new ThrowAfterWriter(_context, 1));
        await Assert
            .That(async () =>
                await store.UpsertWithLibrariesAsync(repository, [library.Id], default)
            )
            .Throws<InvalidOperationException>();
        await Assert.That(repository.LibraryIds).Contains(library.Id);
        await _context.SaveChangesAsync();
        await Assert
            .That(await _context.CodeRepositories.AnyAsync(row => row.Id == repository.Id))
            .IsFalse();
    }

    [Test]
    public async Task Update_NotionResyncIntent_PreservesNewerScheduleUnlessRequested()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var stale = now.AddDays(1);
        var newer = now.AddDays(2);
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "notion",
            SourceKind = RepositorySourceKind.Notion.ToString(),
            NextFullResyncAt = stale,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        await _context
            .Libraries.Where(l => l.Id == library.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.NextFullResyncAt, newer));
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var unchanged = await store.TryUpdateLibraryAsync(
            library,
            requestFullResync: false,
            default
        );
        await Assert
            .That(unchanged!.NextFullResyncAt)
            .IsEqualTo(newer.TruncateToPostgresPrecision());
        var beforeRequest = DateTimeOffset.UtcNow.AddSeconds(-1);
        var requested = await store.TryUpdateLibraryAsync(
            library,
            requestFullResync: true,
            default
        );
        await Assert.That(requested!.NextFullResyncAt!.Value).IsGreaterThanOrEqualTo(beforeRequest);
        await Assert.That(requested.NextFullResyncAt!.Value).IsLessThan(newer);
        await Assert
            .That(await _context.OrganizationAuditEvents.AnyAsync(e => e.SubjectId == library.Id))
            .IsFalse();
    }

    [Test]
    public async Task TryUpdate_MissingLibrary_ReturnsNullAndRecordsNothing()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "missing",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var result = await store.TryUpdateLibraryAsync(library, requestFullResync: true, default);
        await Assert.That(result).IsNull();
        await Assert
            .That(await _context.OrganizationAuditEvents.AnyAsync(e => e.SubjectId == library.Id))
            .IsFalse();
    }

    [Test]
    public async Task Create_WriterAppendsThenThrows_DoesNotLeaveOrphanFact()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "rejected",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var store = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new AppendThenThrowWriter(_context)
        );
        await Assert
            .That(async () => await store.CreateLibraryAsync(library, default))
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        await Assert.That(await _context.Libraries.AnyAsync(row => row.Id == library.Id)).IsFalse();
        await Assert
            .That(
                await _context.OrganizationAuditEvents.AnyAsync(row => row.SubjectId == library.Id)
            )
            .IsFalse();
    }

    private sealed class AppendThenThrowWriter(PostgresDbContext db) : IAuditTrailWriter
    {
        private readonly PostgresAuditTrailWriter inner = new(db, new AuditActorContext());

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
        {
            inner.AppendOrganization(organizationId, teamId, change);
            throw new InvalidOperationException("injected failure after append");
        }

        public void AppendSystem(AuditChange change) => throw new NotSupportedException();
    }

    [Test]
    public async Task CreateAndTrackedUpdate_AuditFailure_CannotBeSavedByCaller()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var failing = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new ThrowAfterWriter(_context, 1)
        );
        var rejected = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "rejected",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await Assert
            .That(async () => await failing.CreateLibraryAsync(rejected, default))
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        await Assert.That(await _context.Libraries.AnyAsync(l => l.Id == rejected.Id)).IsFalse();
        var valid = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "before",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(valid);
        await _context.SaveChangesAsync();
        valid.Name = "after";
        var failingUpdate = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new ThrowAfterWriter(_context, 1)
        );
        await Assert
            .That(async () => await failingUpdate.UpdateLibraryAsync(valid, default))
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await Assert
            .That((await _context.Libraries.SingleAsync(l => l.Id == valid.Id)).Name)
            .IsEqualTo("before");
        await Assert
            .That(
                await _context.OrganizationAuditEvents.AnyAsync(e =>
                    e.OrganizationId == seed.Organization.Id
                )
            )
            .IsFalse();
    }

    [Test]
    public async Task RepositoryLinkReplacement_DoesNotOverwriteNewerRepositorySettings()
    {
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repository)
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        await _context
            .CodeRepositories.Where(r => r.Id == repository.Id)
            .ExecuteUpdateAsync(setters =>
                setters.SetProperty(r => r.DisplayName, "newer settings")
            );
        var store = new PostgresCodeRepositoryStore(_context);
        var result = await store.ReplaceRepositoryLibrariesAsync(
            seed.Organization.Id,
            repository.Id,
            [library.Id],
            default
        );
        await Assert.That(result!.DisplayName).IsEqualTo("newer settings");
        _context.ChangeTracker.Clear();
        var saved = await _context.CodeRepositories.SingleAsync(r => r.Id == repository.Id);
        await Assert.That(saved.DisplayName).IsEqualTo("newer settings");
        await Assert.That(saved.LibraryIds).Contains(library.Id);
    }

    [Test]
    public async Task LibraryMappingNoOp_DiscardsStaleTrackedLinks()
    {
        var libraryId = SeedContext.NewId("library");
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repo, p => p.LibraryIds = [libraryId])
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        _context.Libraries.Add(
            new Library
            {
                Id = libraryId,
                OrganizationId = seed.Organization.Id,
                Name = "docs",
                CreatedAt = now,
                UpdatedAt = now,
            }
        );
        await _context.SaveChangesAsync();
        repo.LibraryIds = ["stale-link"];
        await new PostgresCodeRepositoryStore(_context).ReplaceLibraryRepositoriesAsync(
            seed.Organization.Id,
            libraryId,
            [repo.Id],
            default
        );
        await _context.SaveChangesAsync();
        var saved = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == repo.Id);
        await Assert.That(saved.LibraryIds).IsEquivalentTo(new[] { libraryId });
        await Assert
            .That(
                await _context.OrganizationAuditEvents.CountAsync(e =>
                    e.SubjectId == repo.Id && e.EventType == "repository.library_mapping_changed"
                )
            )
            .IsEqualTo(0);
    }

    [Test]
    public async Task LibraryMapping_CrossOrganizationPendingLinks_RejectsAndPreservesCallerState()
    {
        await TestBiome.Start(_context, out var first).AddRepository(out var selected).BuildAsync();
        await TestBiome.Start(_context, out var second).AddRepository(out var other).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = first.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        var original = other.LibraryIds.ToArray();
        other.LibraryIds = ["unsaved-link"];
        var store = new PostgresCodeRepositoryStore(_context);
        await Assert
            .That(async () =>
                await store.ReplaceLibraryRepositoriesAsync(
                    first.Organization.Id,
                    library.Id,
                    [selected.Id],
                    default
                )
            )
            .Throws<InvalidOperationException>();
        await Assert.That(other.LibraryIds).IsEquivalentTo(new[] { "unsaved-link" });
        await Assert.That(_context.Entry(other).Property(r => r.LibraryIds).IsModified).IsTrue();
        var savedOther = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == other.Id);
        var savedSelected = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == selected.Id);
        await Assert.That(savedOther.LibraryIds).IsEquivalentTo(original);
        await Assert.That(savedSelected.LibraryIds).DoesNotContain(library.Id);
        await Assert
            .That(
                await _context.OrganizationAuditEvents.CountAsync(e =>
                    e.SubjectId == selected.Id
                    && e.EventType == "repository.library_mapping_changed"
                )
            )
            .IsEqualTo(0);
    }

    [Test]
    [Arguments("replace")]
    [Arguments("settings")]
    [Arguments("upsert")]
    [Arguments("delete")]
    public async Task MappingBoundaries_RejectUnrelatedPendingLinks(string operation)
    {
        await TestBiome.Start(_context, out var first).AddRepository(out var repo).BuildAsync();
        await TestBiome.Start(_context, out var second).AddRepository(out var other).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = first.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        other.LibraryIds = ["pending-unrelated"];
        var store = new PostgresCodeRepositoryStore(_context);
        await Assert
            .That(async () =>
            {
                if (operation == "replace")
                    await store.ReplaceRepositoryLibrariesAsync(
                        first.Organization.Id,
                        repo.Id,
                        [library.Id],
                        default
                    );
                else if (operation == "settings")
                    await store.UpdateSettingsWithLibrariesAsync(
                        first.Organization.Id,
                        repo.Id,
                        new RepositorySettingsPatch(null, true, null, null),
                        [library.Id],
                        default
                    );
                else if (operation == "upsert")
                    await store.UpsertWithLibrariesAsync(repo, null, default);
                else
                    await new PostgresLibraryDocumentStore(
                        _context,
                        new DocumentSearchScope()
                    ).DeleteLibraryAsync(first.Organization.Id, library.Id, default);
            })
            .Throws<InvalidOperationException>();
        await Assert.That(other.LibraryIds).IsEquivalentTo(new[] { "pending-unrelated" });
        await Assert
            .That(
                (
                    await _context
                        .CodeRepositories.AsNoTracking()
                        .SingleAsync(r => r.Id == other.Id)
                ).LibraryIds
            )
            .IsEmpty();
        await Assert
            .That(await _context.Libraries.AsNoTracking().AnyAsync(l => l.Id == library.Id))
            .IsTrue();
    }

    [Test]
    public async Task RepositoryUpsert_RejectsPendingTargetLinksInsteadOfDiscardingThem()
    {
        await TestBiome.Start(_context, out var seed).AddRepository(out var repo).BuildAsync();
        repo.LibraryIds = ["pending-target"];
        await Assert
            .That(async () =>
                await new PostgresCodeRepositoryStore(_context).UpsertWithLibrariesAsync(
                    repo,
                    null,
                    default
                )
            )
            .Throws<InvalidOperationException>();
        await Assert.That(repo.LibraryIds).IsEquivalentTo(new[] { "pending-target" });
        await Assert
            .That(
                (
                    await _context.CodeRepositories.AsNoTracking().SingleAsync(r => r.Id == repo.Id)
                ).LibraryIds
            )
            .IsEmpty();
    }

    [Test]
    public async Task RepositorySettingsPatch_UsesLockedValuesAndAppliesExplicitStaleValues()
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
        await TestBiome.Start(seedDb, out var seed).AddRepository(out var repo).BuildAsync();
        var gate = new SqlBarrier("code_review_repositories", "FOR UPDATE", pause: true);
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
            .AddInterceptors(gate)
            .Options;
        await using var waitingDb = new PostgresDbContext(options);
        var tracked = await waitingDb.CodeRepositories.SingleAsync(r => r.Id == repo.Id);
        var oldEnabled = tracked.Enabled;
        var oldVisible = tracked.VisibleInLibraryPicker;
        var operation = new PostgresCodeRepositoryStore(waitingDb).UpdateSettingsWithLibrariesAsync(
            seed.Organization.Id,
            repo.Id,
            new RepositorySettingsPatch(null, oldEnabled, null, null),
            null,
            default
        );
        await gate.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var writerDb = isolated.CreateContext();
        await writerDb
            .CodeRepositories.Where(r => r.Id == repo.Id)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(r => r.DisplayName, "concurrent name")
                    .SetProperty(r => r.Enabled, !oldEnabled)
                    .SetProperty(r => r.VisibleInLibraryPicker, !oldVisible)
            );
        var concurrent = await writerDb.CodeRepositories.SingleAsync(r => r.Id == repo.Id);
        concurrent.ReviewConfiguration.SharedPromptFragment = "new review guidance";
        await writerDb.SaveChangesAsync();
        gate.Resume.SetResult();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(result!.DisplayName).IsEqualTo("concurrent name");
        var saved = await writerDb
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == repo.Id);
        await Assert.That(saved.DisplayName).IsEqualTo("concurrent name");
        await Assert.That(saved.VisibleInLibraryPicker).IsEqualTo(!oldVisible);
        await Assert.That(saved.Enabled).IsEqualTo(oldEnabled);
        await Assert
            .That(saved.ReviewConfiguration.SharedPromptFragment)
            .IsEqualTo("new review guidance");
        await writerDb
            .CodeRepositories.Where(r => r.Id == repo.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayName, "another name"));
        await new PostgresCodeRepositoryStore(waitingDb).UpdateSettingsWithLibrariesAsync(
            seed.Organization.Id,
            repo.Id,
            new RepositorySettingsPatch(null, oldEnabled, "concurrent name", null),
            null,
            default
        );
        saved = await writerDb.CodeRepositories.AsNoTracking().SingleAsync(r => r.Id == repo.Id);
        await Assert.That(saved.DisplayName).IsEqualTo("concurrent name");
    }

    [Test]
    public async Task RepositoryUpsertWithLibraries_CreateUpdateAndNullPreserve_RecordActualChanges()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        var repo = new CodeRepository
        {
            Id = SeedContext.NewId("repo"),
            OrganizationId = seed.Organization.Id,
            Provider = "github",
            OwnerQualifiedName = "example/repo",
            DisplayName = "before",
            Enabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LibraryIds = [library.Id],
        };
        var store = new PostgresCodeRepositoryStore(_context);
        var created = await store.UpsertWithLibrariesAsync(repo, [library.Id], default);
        await Assert.That(created.LibraryIds).Contains(library.Id);
        var immediatelySaved = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == repo.Id);
        await Assert.That(immediatelySaved.LibraryIds).Contains(library.Id);
        await Assert
            .That(
                await _context
                    .OrganizationAuditEvents.AsNoTracking()
                    .CountAsync(e =>
                        e.SubjectId == repo.Id
                        && e.EventType == "repository.library_mapping_changed"
                    )
            )
            .IsEqualTo(1);
        created.DisplayName = "after";
        var updated = await store.UpsertWithLibrariesAsync(created, null, default);
        await Assert.That(updated.LibraryIds).Contains(library.Id);
        await store.UpsertWithLibrariesAsync(created, [], default);
        var events = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e =>
                e.OrganizationId == seed.Organization.Id
                && e.EventType == "repository.library_mapping_changed"
            )
            .ToArrayAsync();
        await Assert.That(events.Length).IsEqualTo(2);
        await Assert
            .That(
                (
                    await _context.CodeRepositories.AsNoTracking().SingleAsync(r => r.Id == repo.Id)
                ).LibraryIds
            )
            .IsEmpty();
    }

    [Test]
    public async Task ConcurrentMappingReplacementAndDelete_SerializeWithoutDanglingLinks()
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
        await TestBiome.Start(seedDb, out var seed).AddRepository(out var repo).BuildAsync();
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
        await seedDb.SaveChangesAsync();
        await using var firstDb = isolated.CreateContext();
        await using var secondDb = isolated.CreateContext();
        await new PostgresCodeRepositoryStore(seedDb).ReplaceLibraryRepositoriesAsync(
            seed.Organization.Id,
            library.Id,
            [repo.Id],
            default
        );
        await using (var transaction = await firstDb.Database.BeginTransactionAsync())
        {
            var first = new PostgresCodeRepositoryStore(firstDb);
            var second = new PostgresCodeRepositoryStore(secondDb);
            await first.ReplaceLibraryRepositoriesAsync(
                seed.Organization.Id,
                library.Id,
                [],
                default
            );
            var replacement = second.ReplaceLibraryRepositoriesAsync(
                seed.Organization.Id,
                library.Id,
                [repo.Id],
                default
            );
            await Assert
                .That(await Task.WhenAny(replacement, Task.Delay(200)) == replacement)
                .IsFalse();
            await transaction.CommitAsync();
            await replacement;
        }
        secondDb.ChangeTracker.Clear();
        await Assert
            .That(
                (
                    await secondDb.CodeRepositories.AsNoTracking().SingleAsync(r => r.Id == repo.Id)
                ).LibraryIds
            )
            .Contains(library.Id);
        await Assert
            .That(
                await secondDb.OrganizationAuditEvents.CountAsync(e =>
                    e.EventType == "repository.library_mapping_changed"
                    && e.OrganizationId == seed.Organization.Id
                )
            )
            .IsEqualTo(3);
        await using (var transaction = await firstDb.Database.BeginTransactionAsync())
        {
            var deleting = new PostgresLibraryDocumentStore(firstDb, new DocumentSearchScope());
            await deleting.DeleteLibraryAsync(seed.Organization.Id, library.Id, default);
            var repository = await secondDb
                .CodeRepositories.AsNoTracking()
                .SingleAsync(r => r.Id == repo.Id);
            var mapping = new PostgresCodeRepositoryStore(secondDb).UpsertWithLibrariesAsync(
                repository,
                [library.Id],
                default
            );
            await Assert.That(await Task.WhenAny(mapping, Task.Delay(200)) == mapping).IsFalse();
            await transaction.CommitAsync();
            await Assert.That(async () => await mapping).Throws<ArgumentException>();
        }
        secondDb.ChangeTracker.Clear();
        await Assert
            .That(
                (
                    await secondDb.CodeRepositories.AsNoTracking().SingleAsync(r => r.Id == repo.Id)
                ).LibraryIds
            )
            .IsEmpty();
        await Assert.That(await secondDb.Libraries.AnyAsync(l => l.Id == library.Id)).IsFalse();
    }

    [Test]
    public async Task Delete_SecondBatchAuditFailure_CallerCanContinueWithoutPartialDeletion()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        for (var i = 0; i < 205; i++)
            _context.LibraryDocuments.Add(
                new LibraryDocument
                {
                    Id = SeedContext.NewId("document"),
                    OrganizationId = seed.Organization.Id,
                    LibraryId = library.Id,
                    Path = $"/{i}.md",
                    Title = "Title",
                    TitleNormalized = "title",
                    Content = "body",
                    ContentHash = $"hash-{i}",
                    Keywords = [],
                    Headings = [],
                    CreatedAt = now,
                    UpdatedAt = now,
                }
            );
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var callerLibrary = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "earlier",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(callerLibrary);
        var store = new PostgresLibraryDocumentStore(
            _context,
            new DocumentSearchScope(),
            new ThrowAfterWriter(_context, 202)
        );
        await Assert
            .That(async () =>
                await store.DeleteLibraryAsync(seed.Organization.Id, library.Id, default)
            )
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await Assert.That(await _context.Libraries.AnyAsync(l => l.Id == library.Id)).IsTrue();
        await Assert
            .That(await _context.Libraries.AnyAsync(l => l.Id == callerLibrary.Id))
            .IsTrue();
        await Assert
            .That(await _context.LibraryDocuments.CountAsync(d => d.LibraryId == library.Id))
            .IsEqualTo(205);
        await Assert
            .That(
                await _context.OrganizationAuditEvents.AnyAsync(e =>
                    e.OrganizationId == seed.Organization.Id
                )
            )
            .IsFalse();
    }

    [Test]
    public async Task RepositorySettingsAndLinks_AuditFailure_RollsBackBothWhenCallerContinues()
    {
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repository)
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "docs",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        var oldName = repository.DisplayName;
        var requested = new CodeRepository
        {
            Id = repository.Id,
            OrganizationId = repository.OrganizationId,
            Provider = repository.Provider,
            OwnerQualifiedName = repository.OwnerQualifiedName,
            DisplayName = "new name",
            Enabled = repository.Enabled,
            VisibleInLibraryPicker = repository.VisibleInLibraryPicker,
            ReviewConfiguration = repository.ReviewConfiguration,
            CreatedAtUtc = repository.CreatedAtUtc,
            UpdatedAtUtc = now,
        };
        var store = new PostgresCodeRepositoryStore(_context, new ThrowAfterWriter(_context, 1));
        await Assert
            .That(async () =>
                await store.UpsertWithLibrariesAsync(requested, [library.Id], default)
            )
            .Throws<InvalidOperationException>();
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var persisted = await _context.CodeRepositories.SingleAsync(r => r.Id == repository.Id);
        await Assert.That(persisted.DisplayName).IsEqualTo(oldName);
        await Assert.That(persisted.LibraryIds).IsEmpty();
        await Assert
            .That(
                await _context.OrganizationAuditEvents.AnyAsync(e =>
                    e.OrganizationId == seed.Organization.Id
                )
            )
            .IsFalse();
    }

    private sealed class ThrowAfterWriter(PostgresDbContext db, int failAt) : IAuditTrailWriter
    {
        private readonly PostgresAuditTrailWriter inner = new(db, new AuditActorContext());
        private int count;

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
        {
            if (++count == failAt)
                throw new InvalidOperationException("injected failure");
            inner.AppendOrganization(organizationId, teamId, change);
        }

        public void AppendSystem(AuditChange change) => inner.AppendSystem(change);
    }

    [Test]
    public async Task Delete_MixedChildrenAndMappings_RecordsOneGroupedOperation()
    {
        var libraryId = SeedContext.NewId("library");
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repository, p => p.LibraryIds = [libraryId])
            .BuildAsync();
        var actor = new AuditActorContext();
        var writer = new PostgresAuditTrailWriter(_context, actor);
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope(), writer);
        var now = DateTimeOffset.UtcNow;
        var library = await store.CreateLibraryAsync(
            new Library
            {
                Id = libraryId,
                OrganizationId = seed.Organization.Id,
                Name = "docs",
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
        for (var i = 0; i < 205; i++)
        {
            _context.LibraryDocuments.Add(
                new LibraryDocument
                {
                    Id = SeedContext.NewId("document"),
                    OrganizationId = seed.Organization.Id,
                    LibraryId = library.Id,
                    Path = $"/{i}.md",
                    Title = "Title",
                    TitleNormalized = "title",
                    Content = "private body",
                    ContentHash = $"hash-{i}",
                    SyncRunId = i == 0 ? "run1" : null,
                    Keywords = [],
                    Headings = [],
                    CreatedAt = now,
                    UpdatedAt = now,
                }
            );
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await store.DeleteLibraryAsync(seed.Organization.Id, libraryId, default);
        var facts = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e =>
                e.OrganizationId == seed.Organization.Id && e.EventType != "library.created"
            )
            .ToArrayAsync();
        var parent = facts.Single(e => e.EventType == "library.deleted");
        var before = AuditSnapshotSerializer.Deserialize(parent.BeforeSnapshot)!;
        await Assert.That(before.Fields["documentCount"]).IsEqualTo("205");
        await Assert.That(before.Fields["repositoryMappingCount"]).IsEqualTo("1");
        await Assert.That(facts.Count(e => e.EventType == "document.deleted")).IsEqualTo(205);
        await Assert
            .That(facts.Count(e => e.EventType == "repository.library_mapping_changed"))
            .IsEqualTo(1);
        await Assert
            .That(facts.All(e => e.OperationId == actor.OperationId && e.LibraryId == libraryId))
            .IsTrue();
        var snapshots = facts
            .Where(e => e.EventType == "document.deleted")
            .Select(e => AuditSnapshotSerializer.Deserialize(e.BeforeSnapshot)!)
            .ToArray();
        await Assert.That(snapshots.Count(s => s.Content == "private body")).IsEqualTo(204);
        await Assert.That(snapshots.Count(s => s.Content is null)).IsEqualTo(1);
        await Assert.That(await _context.Libraries.AnyAsync(l => l.Id == libraryId)).IsFalse();
        await Assert
            .That(
                (
                    await _context
                        .CodeRepositories.AsNoTracking()
                        .SingleAsync(r => r.Id == repository.Id)
                ).LibraryIds
            )
            .DoesNotContain(libraryId);
    }

    [Test]
    public async Task Update_TrackedStaleLibrary_UsesDatabasePreimageAndSuppressesRepeatEvent()
    {
        await TestBiome.Start(_context, out var seed).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var now = DateTimeOffset.UtcNow;
        var library = await store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = seed.Organization.Id,
                Name = "old",
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
        await _context
            .Libraries.Where(l => l.Id == library.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Description, "database"));
        library.Description = "requested";
        await store.UpdateLibraryAsync(library, default);
        await store.UpdateLibraryAsync(library, default);
        var updates = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e => e.SubjectId == library.Id && e.EventType == "library.updated")
            .ToArrayAsync();
        await Assert.That(updates.Length).IsEqualTo(1);
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(updates[0].BeforeSnapshot)!.Fields[
                    "description"
                ]
            )
            .IsEqualTo("database");
        await Assert
            .That(
                AuditSnapshotSerializer.Deserialize(updates[0].AfterSnapshot)!.Fields["description"]
            )
            .IsEqualTo("requested");
    }

    [Test]
    public async Task MappingReplace_EmitsOnlyChangedRelationships_AndGenericSaveCannotRestoreStaleLinks()
    {
        await TestBiome
            .Start(_context, out var seed)
            .AddRepository(out var repository)
            .BuildAsync();
        var libraries = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var repos = new PostgresCodeRepositoryStore(_context);
        var now = DateTimeOffset.UtcNow;
        var library = await libraries.CreateLibraryAsync(
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
        await repos.ReplaceLibraryRepositoriesAsync(
            seed.Organization.Id,
            library.Id,
            [repository.Id],
            default
        );
        var persistedTimestamp = await _context
            .CodeRepositories.AsNoTracking()
            .Where(row => row.Id == repository.Id)
            .Select(row => row.UpdatedAtUtc)
            .SingleAsync();
        await Assert.That(repository.UpdatedAtUtc).IsEqualTo(persistedTimestamp);
        await Assert
            .That(_context.Entry(repository).Property(row => row.UpdatedAtUtc).IsModified)
            .IsFalse();
        repository.DisplayName = "unrelated later change";
        await _context.SaveChangesAsync();
        await Assert
            .That(
                await _context
                    .CodeRepositories.AsNoTracking()
                    .Where(row => row.Id == repository.Id)
                    .Select(row => row.UpdatedAtUtc)
                    .SingleAsync()
            )
            .IsEqualTo(persistedTimestamp);
        await repos.ReplaceLibraryRepositoriesAsync(
            seed.Organization.Id,
            library.Id,
            [repository.Id],
            default
        );
        await repos.ReplaceLibraryRepositoriesAsync(seed.Organization.Id, library.Id, [], default);
        repository.LibraryIds = [library.Id];
        await repos.UpsertAsync(repository, default);
        _context.ChangeTracker.Clear();
        var facts = await _context
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e =>
                e.OrganizationId == seed.Organization.Id
                && e.EventType == "repository.library_mapping_changed"
            )
            .ToArrayAsync();
        await Assert.That(facts.Length).IsEqualTo(2);
        await Assert
            .That(
                (
                    await _context
                        .CodeRepositories.AsNoTracking()
                        .SingleAsync(r => r.Id == repository.Id)
                ).LibraryIds
            )
            .IsEmpty();
        // A replay after an intervening unmap is a new real change, not a safe no-op receipt.
        await repos.ReplaceLibraryRepositoriesAsync(
            seed.Organization.Id,
            library.Id,
            [repository.Id],
            default
        );
        await Assert
            .That(
                await _context.OrganizationAuditEvents.CountAsync(e =>
                    e.OrganizationId == seed.Organization.Id
                    && e.EventType == "repository.library_mapping_changed"
                )
            )
            .IsEqualTo(3);
    }
}
