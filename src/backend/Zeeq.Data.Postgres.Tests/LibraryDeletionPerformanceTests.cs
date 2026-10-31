using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>Isolated large-delete probe for transaction cost and bounded audit tracking.</summary>
/// <remarks>
/// Run explicitly with the filter for this class. One local sample is not a capacity/SLA result.
/// The probe refuses the opt-in shared database; it owns and removes a fresh container.
/// </remarks>
[Explicit]
[NotInParallel]
public sealed class LibraryDeletionPerformanceTests
{
    [Test]
    public async Task MeasureFiveThousandDocumentDeletion()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
        {
            throw new InvalidOperationException("This probe requires its own disposable database.");
        }

        await using var postgres = new PgDatabaseFixture();
        await postgres.InitializeAsync();
        await using var db = postgres.CreateContext();
        var libraryId = SeedContext.NewId("library");
        await TestBiome
            .Start(db, out var seed)
            .AddRepository(out _, repository => repository.LibraryIds = [libraryId])
            .BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = libraryId,
            OrganizationId = seed.Organization.Id,
            Name = "large-delete-probe",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        const int documentCount = 5_000;
        var body = string.Concat(
            Enumerable.Repeat("Local document text for deletion capture. ", 50)
        );
        for (var n = 0; n < documentCount; n++)
        {
            db.LibraryDocuments.Add(
                new LibraryDocument
                {
                    Id = SeedContext.NewId("document"),
                    OrganizationId = seed.Organization.Id,
                    LibraryId = libraryId,
                    Path = $"/docs/{n:D5}.md",
                    Title = $"Document {n}",
                    TitleNormalized = $"document {n}",
                    Content = body,
                    ContentHash = $"probe-hash-{n}",
                    Keywords = [],
                    Headings = [],
                    CreatedAt = now,
                    UpdatedAt = now,
                }
            );
            if ((n + 1) % 500 == 0)
            {
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
        }

        var writer = new TrackingWriter(db);
        var store = new PostgresLibraryDocumentStore(db, new DocumentSearchScope(), writer);
        var beforeWal = await db
            .Database.SqlQuery<string>($"SELECT pg_current_wal_lsn()::text AS \"Value\"")
            .SingleAsync();
        var beforeHeap = GC.GetTotalMemory(forceFullCollection: false);
        var beforeAllocation = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        await store.DeleteLibraryAsync(seed.Organization.Id, libraryId, default);
        timer.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocation;
        var afterHeap = GC.GetTotalMemory(forceFullCollection: false);
        var afterWal = await db
            .Database.SqlQuery<string>($"SELECT pg_current_wal_lsn()::text AS \"Value\"")
            .SingleAsync();
        var wal = await db
            .Database.SqlQuery<decimal>(
                $"SELECT pg_wal_lsn_diff({afterWal}::pg_lsn, {beforeWal}::pg_lsn)::numeric AS \"Value\""
            )
            .SingleAsync();
        var facts = await db.OrganizationAuditEvents.CountAsync();
        var parent = await db
            .OrganizationAuditEvents.AsNoTracking()
            .SingleAsync(row => row.EventType == "library.deleted");
        var before = AuditSnapshotSerializer.Deserialize(parent.BeforeSnapshot)!;

        await Assert.That(await db.LibraryDocuments.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.Libraries.CountAsync()).IsEqualTo(0);
        await Assert.That(facts).IsEqualTo(documentCount + 2);
        await Assert.That(before.Fields["documentCount"]).IsEqualTo("5000");
        await Assert.That(before.Fields["repositoryMappingCount"]).IsEqualTo("1");
        await Assert.That(writer.MaxTrackedFacts).IsLessThanOrEqualTo(201);
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    probe = "library-deletion",
                    environment = new
                    {
                        runtime = Environment.Version.ToString(),
                        processors = Environment.ProcessorCount,
                        isolatedContainer = true,
                    },
                    documentCount,
                    contentCharactersPerDocument = body.Length,
                    facts,
                    milliseconds = timer.Elapsed.TotalMilliseconds,
                    allocatedBytes = allocated,
                    managedHeapBeforeBytes = beforeHeap,
                    managedHeapAfterBytes = afterHeap,
                    writer.MaxTrackedFacts,
                    clusterWalBytes = wal,
                    caveat = "One local sample with tracker-count instrumentation included in time and allocations. Total allocations are not peak memory. Cluster WAL includes background work. Locks and WAL span the whole delete transaction despite bounded batches.",
                }
            )
        );
    }

    private sealed class TrackingWriter(PostgresDbContext db) : IAuditTrailWriter
    {
        private readonly PostgresAuditTrailWriter inner = new(db, new AuditActorContext());
        public int MaxTrackedFacts { get; private set; }

        public void AppendOrganization(string organizationId, string? teamId, AuditChange change)
        {
            inner.AppendOrganization(organizationId, teamId, change);
            MaxTrackedFacts = Math.Max(
                MaxTrackedFacts,
                db.ChangeTracker.Entries<OrganizationAuditEvent>().Count()
            );
        }

        public void AppendSystem(AuditChange change) => inner.AppendSystem(change);
    }
}
