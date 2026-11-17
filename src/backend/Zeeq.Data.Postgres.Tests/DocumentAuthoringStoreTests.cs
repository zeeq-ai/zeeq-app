using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.Biomes;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>Tests the atomic insert and compare-and-set authoring operations.</summary>
public sealed class DocumentAuthoringStoreTests
{
    [Test]
    public async Task ConcurrentReplacements_OnlyOneApplies_AndAuditUsesLockedPreimage()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException(
                "Concurrency test requires its own test container."
            );
        await using var postgres = new PgDatabaseFixture();
        await postgres.InitializeAsync();
        await using var seedDb = postgres.CreateContext();
        await TestBiome.Start(seedDb, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "authoring",
            CreatedAt = now,
            UpdatedAt = now,
        };
        seedDb.Libraries.Add(library);
        var doc = NewDocument(seed.Organization.Id, library.Id, "/one.md", "old", "hash-old");
        seedDb.LibraryDocuments.Add(doc);
        await seedDb.SaveChangesAsync();

        await using var db1 = postgres.CreateContext();
        await using var db2 = postgres.CreateContext();
        await using var transaction = await db1.Database.BeginTransactionAsync();
        var first = await db1.LibraryDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id);
        var second = await db2.LibraryDocuments.AsNoTracking().SingleAsync(d => d.Id == doc.Id);
        first.Content = "first";
        first.ContentHash = "hash-old"; // A caller cannot forge the stored hash.
        second.Content = "second";
        second.ContentHash = "hash-second";
        var actor = new AuditActor("user", "user-1", "User", "mcp", CredentialName: "client");
        var store1 = new PostgresLibraryDocumentStore(db1, new DocumentSearchScope());
        var store2 = new PostgresLibraryDocumentStore(db2, new DocumentSearchScope());
        var applied = await store1.TryReplaceContentAsync(
            first,
            "hash-old",
            "reason",
            actor,
            "client",
            default
        );
        var pending = store2.TryReplaceContentAsync(
            second,
            "hash-old",
            "second reason",
            actor,
            "client",
            default
        );
        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(250)));
        await Assert.That(completed == pending).IsFalse();
        await transaction.CommitAsync();
        var stale = await pending;
        await Assert.That(applied is ReplaceContentResult.Applied).IsTrue();
        await Assert.That(stale is ReplaceContentResult.Stale).IsTrue();
        await using var check = postgres.CreateContext();
        var saved = await check.LibraryDocuments.SingleAsync(d => d.Id == doc.Id);
        var audit = await check.OrganizationAuditEvents.SingleAsync(e =>
            e.SubjectId == doc.Id && e.EventType == "document.updated"
        );
        await Assert.That(saved.Content).IsEqualTo("first");
        await Assert.That(saved.ContentHash).IsEqualTo(DocumentContentHash.Compute("first"));
        await Assert.That(audit.BeforeHash).IsEqualTo("hash-old");
        await Assert.That(audit.AfterHash).IsEqualTo(DocumentContentHash.Compute("first"));
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.BeforeSnapshot)!.Content)
            .IsEqualTo("old");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)!.Fields["rationale"])
            .IsEqualTo("reason");
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audit.AfterSnapshot)!.Fields["mcpClientName"])
            .IsEqualTo("client");
        await Assert.That(audit.ActorCredentialName).IsEqualTo("client");
    }

    [Test]
    public async Task CreateConflictAndIdentityOrOwnershipChange_DoNotOverwrite()
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException("Authoring test requires its own test container.");
        await using var postgres = new PgDatabaseFixture();
        await postgres.InitializeAsync();
        await using var db = postgres.CreateContext();
        await TestBiome.Start(db, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "authoring",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var store = new PostgresLibraryDocumentStore(db, new DocumentSearchScope());
        var actor = AuditActor.Worker("test");
        var doc = NewDocument(seed.Organization.Id, library.Id, "/one.md", "first", "hash");
        var created = await store.CreateAgentDocumentAsync(doc, "new", actor, "client", default);
        var duplicate = NewDocument(
            seed.Organization.Id,
            library.Id,
            "/one.md",
            "other",
            "other-hash"
        );
        var expectedHash = DocumentContentHash.Compute("first");
        await Assert.That(doc.ContentHash).IsEqualTo(expectedHash);
        await Assert
            .That(async () =>
                await store.CreateAgentDocumentAsync(doc, "again", actor, "client", default)
            )
            .Throws<ArgumentException>();
        await Assert.That(doc.Authoring).IsEqualTo(LibraryDocumentAuthoring.Agent);
        await Assert.That(doc.ContentHash).IsEqualTo(expectedHash);
        await db.SaveChangesAsync();
        var unchanged = await store.TryReplaceContentAsync(
            doc,
            expectedHash,
            "no change",
            actor,
            "client",
            default
        );
        await Assert.That(unchanged is ReplaceContentResult.Applied).IsTrue();
        var exists = await store.CreateAgentDocumentAsync(
            duplicate,
            "duplicate",
            actor,
            "client",
            default
        );
        var failed = NewDocument(
            seed.Organization.Id,
            library.Id,
            "/failed.md",
            "failed",
            "original-hash"
        );
        var failingStore = new PostgresLibraryDocumentStore(
            db,
            new DocumentSearchScope(),
            new ThrowingAuditWriter()
        );
        await Assert
            .That(async () =>
                await failingStore.CreateAgentDocumentAsync(
                    failed,
                    "failure",
                    actor,
                    "client",
                    default
                )
            )
            .Throws<InvalidOperationException>();
        await Assert.That(failed.Authoring).IsEqualTo(LibraryDocumentAuthoring.Manual);
        await Assert.That(failed.ContentHash).IsEqualTo("original-hash");
        await Assert
            .That(await db.LibraryDocuments.AsNoTracking().AnyAsync(d => d.Id == failed.Id))
            .IsFalse();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledDoc = NewDocument(
            seed.Organization.Id,
            library.Id,
            "/cancelled.md",
            "cancelled",
            "initial-hash"
        );
        await Assert
            .That(async () =>
                await store.CreateAgentDocumentAsync(
                    cancelledDoc,
                    "cancelled",
                    actor,
                    "client",
                    cancelled.Token
                )
            )
            .Throws<OperationCanceledException>();
        await Assert.That(cancelledDoc.Authoring).IsEqualTo(LibraryDocumentAuthoring.Manual);
        await Assert.That(cancelledDoc.ContentHash).IsEqualTo("initial-hash");
        var identity = await store.TryReplaceContentAsync(
            duplicate,
            expectedHash,
            "edit",
            actor,
            "client",
            default
        );
        await db
            .LibraryDocuments.Where(d => d.Id == doc.Id)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(d => d.Authoring, LibraryDocumentAuthoring.Synced)
            );
        var ineligible = await store.TryReplaceContentAsync(
            doc,
            expectedHash,
            "edit",
            actor,
            "client",
            default
        );
        await db
            .LibraryDocuments.Where(d => d.Id == doc.Id)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(d => d.Authoring, LibraryDocumentAuthoring.Manual)
                    .SetProperty(d => d.SourceExternalId, "source-page")
            );
        var sourced = await store.TryReplaceContentAsync(
            doc,
            expectedHash,
            "edit",
            actor,
            "client",
            default
        );
        await Assert.That(sourced is ReplaceContentResult.NotEditable).IsTrue();
        await Assert.That(created is CreateDocumentResult.Created).IsTrue();
        await Assert.That(exists is CreateDocumentResult.Exists).IsTrue();
        await Assert.That(duplicate.Authoring).IsEqualTo(LibraryDocumentAuthoring.Manual);
        await Assert.That(duplicate.ContentHash).IsEqualTo("other-hash");
        await Assert.That(identity is ReplaceContentResult.Stale).IsTrue();
        await Assert.That(ineligible is ReplaceContentResult.NotEditable).IsTrue();
        var audits = await db
            .OrganizationAuditEvents.AsNoTracking()
            .Where(e => e.SubjectId == doc.Id)
            .ToArrayAsync();
        await Assert.That(audits.Length).IsEqualTo(1);
        await Assert
            .That(AuditSnapshotSerializer.Deserialize(audits[0].AfterSnapshot)!.Fields["rationale"])
            .IsEqualTo("new");
    }

    [Test]
    [Arguments("stale")]
    [Arguments("not_editable")]
    [Arguments("not_found")]
    [Arguments("unchanged")]
    [Arguments("moved")]
    [Arguments("detached_stale")]
    public async Task TrackedNonAppliedRequest_DoesNotReplayAndPreservesOtherWork(string scenario)
    {
        if (
            !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("ZEEQ_TEST_POSTGRES_CONNECTION_STRING")
            )
        )
            throw new InvalidOperationException("Test requires its own test container.");
        await using var postgres = new PgDatabaseFixture();
        await postgres.InitializeAsync();
        await using var setup = postgres.CreateContext();
        await TestBiome.Start(setup, out var seed).BuildAsync();
        var now = DateTimeOffset.UtcNow;
        var library = new Library
        {
            Id = SeedContext.NewId("library"),
            OrganizationId = seed.Organization.Id,
            Name = "tracker",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var originalHash = DocumentContentHash.Compute("old");
        var target = NewDocument(
            seed.Organization.Id,
            library.Id,
            "/target.md",
            "old",
            originalHash
        );
        var other = NewDocument(
            seed.Organization.Id,
            library.Id,
            "/other.md",
            "other old",
            DocumentContentHash.Compute("other old")
        );
        setup.Libraries.Add(library);
        setup.LibraryDocuments.AddRange(target, other);
        await setup.SaveChangesAsync();

        await using var db = postgres.CreateContext();
        await using var concurrent = postgres.CreateContext();
        var tracked = await db.LibraryDocuments.SingleAsync(d => d.Id == target.Id);
        var unrelated = await db.LibraryDocuments.SingleAsync(d => d.Id == other.Id);
        unrelated.Content = "unrelated pending";
        var request =
            scenario == "detached_stale"
                ? await db.LibraryDocuments.AsNoTracking().SingleAsync(d => d.Id == target.Id)
                : tracked;
        if (scenario == "detached_stale")
        {
            tracked.Content = "independent pending";
            tracked.ContentHash = DocumentContentHash.Compute("independent pending");
        }
        request.Content = scenario == "unchanged" ? "old" : "rejected attempt";
        request.ContentHash = "attempted hash";
        request.Title = "attempted title";
        if (scenario is "stale" or "detached_stale")
            await concurrent
                .LibraryDocuments.Where(d => d.Id == target.Id)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(d => d.Content, "concurrent")
                        .SetProperty(d => d.ContentHash, DocumentContentHash.Compute("concurrent"))
                );
        else if (scenario == "not_editable")
            await concurrent
                .LibraryDocuments.Where(d => d.Id == target.Id)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(d => d.Authoring, LibraryDocumentAuthoring.Synced)
                );
        else if (scenario == "not_found")
            await concurrent.LibraryDocuments.Where(d => d.Id == target.Id).ExecuteDeleteAsync();
        else if (scenario == "moved")
            await concurrent
                .LibraryDocuments.Where(d => d.Id == target.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Path, "/moved.md"));
        var store = new PostgresLibraryDocumentStore(db, new DocumentSearchScope());
        var result = await store.TryReplaceContentAsync(
            request,
            originalHash,
            "rejected",
            AuditActor.Worker("test"),
            "client",
            default
        );
        await Assert
            .That(
                scenario switch
                {
                    "stale" or "detached_stale" => result is ReplaceContentResult.Stale,
                    "moved" => result is ReplaceContentResult.NotFound,
                    "not_editable" => result is ReplaceContentResult.NotEditable,
                    "not_found" => result is ReplaceContentResult.NotFound,
                    "unchanged" => result is ReplaceContentResult.Applied,
                    _ => false,
                }
            )
            .IsTrue();
        await db.SaveChangesAsync();
        await using var check = postgres.CreateContext();
        var saved = await check
            .LibraryDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == target.Id);
        if (scenario == "not_found")
            await Assert.That(saved).IsNull();
        else
        {
            var expectedContent = scenario switch
            {
                "stale" => "concurrent",
                "detached_stale" => "independent pending",
                _ => "old",
            };
            await Assert.That(saved!.Content).IsEqualTo(expectedContent);
            await Assert
                .That(saved.Path)
                .IsEqualTo(scenario == "moved" ? "/moved.md" : "/target.md");
            await Assert
                .That(saved.ContentHash)
                .IsEqualTo(DocumentContentHash.Compute(expectedContent));
            await Assert.That(saved.Title).IsEqualTo("Title");
        }
        var savedOther = await check
            .LibraryDocuments.AsNoTracking()
            .SingleAsync(d => d.Id == other.Id);
        await Assert.That(savedOther.Content).IsEqualTo("unrelated pending");
        await Assert
            .That(await check.OrganizationAuditEvents.CountAsync(e => e.SubjectId == target.Id))
            .IsEqualTo(0);
    }

    private sealed class ThrowingAuditWriter : IAuditTrailWriter
    {
        public void AppendOrganization(string organizationId, string? teamId, AuditChange change) =>
            throw new InvalidOperationException("injected audit failure");

        public void AppendSystem(AuditChange change) =>
            throw new InvalidOperationException("injected audit failure");
    }

    private static LibraryDocument NewDocument(
        string org,
        string library,
        string path,
        string content,
        string hash
    ) =>
        new()
        {
            Id = SeedContext.NewId("document"),
            OrganizationId = org,
            LibraryId = library,
            Path = path,
            Title = "Title",
            TitleNormalized = "title",
            Content = content,
            ContentHash = hash,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
}
