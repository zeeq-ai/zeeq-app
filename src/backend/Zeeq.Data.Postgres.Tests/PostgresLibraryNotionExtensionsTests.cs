using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests covering the Notion-related additions to <see cref="Library"/> and
/// <see cref="LibraryDocument"/>: the <c>external_source</c> jsonb round-trip, external-id
/// document identity, and scheduler pickup of Notion-sourced libraries.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/PostgresLibraryNotionExtensionsTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresLibraryNotionExtensionsTests : PgTransactionalTestBase
{
    public PostgresLibraryNotionExtensionsTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task Library_ExternalSourceJsonb_RoundTripsNotionConfiguration()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var now = DateTimeOffset.UtcNow;

        var created = await store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = seed.Organization.Id,
                Name = "notion-lib",
                SourceKind = "Notion",
                SyncStatus = "idle",
                ExternalSource = new LibraryExternalSource
                {
                    Notion = new NotionSourceConfiguration
                    {
                        ConnectionName = "Engineering Docs",
                        WorkspaceId = "workspace-1",
                        WorkspaceName = "Acme Co",
                        AccessTokenValueId = "encrypted-value-1",
                        CallbackTokenSerial = 1,
                    },
                },
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var reloaded = await store.GetLibraryByIdAsync(
            seed.Organization.Id,
            created.Id,
            CancellationToken.None
        );

        await Assert.That(reloaded).IsNotNull();
        await Assert.That(reloaded!.ExternalSource).IsNotNull();
        await Assert.That(reloaded.ExternalSource!.Notion).IsNotNull();
        await Assert.That(reloaded.ExternalSource.Notion!.ConnectionName).IsEqualTo(
            "Engineering Docs"
        );
        await Assert.That(reloaded.ExternalSource.Notion.AccessTokenValueId).IsEqualTo(
            "encrypted-value-1"
        );
        await Assert.That(reloaded.ExternalSource.Notion.VerificationTokenValueId).IsNull();
    }

    [Test]
    public async Task Library_ClaimDueForSync_IncludesNotionSourcedLibraries()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var now = DateTimeOffset.UtcNow;

        var library = await store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = seed.Organization.Id,
                Name = "notion-lib",
                SourceKind = "Notion",
                SyncStatus = "idle",
                NextSyncAt = now.AddMinutes(-5),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();

        var claimed = await store.ClaimDueForSyncAsync(10, CancellationToken.None);

        await Assert.That(claimed.Any(row => row.Id == library.Id)).IsTrue();
    }

    [Test]
    public async Task LibraryDocument_GetByExternalId_ResolvesAcrossPathRename()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var libraryStore = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var now = DateTimeOffset.UtcNow;
        var library = await libraryStore.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = seed.Organization.Id,
                Name = "notion-lib",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None
        );

        var documentId = SeedContext.NewId("doc");
        var upsertResult = await libraryStore.UpsertSyncedDocumentAsync(
            new LibraryDocument
            {
                Id = documentId,
                OrganizationId = seed.Organization.Id,
                LibraryId = library.Id,
                Path = "engineering/initiatives",
                Title = "Initiatives",
                TitleNormalized = "initiatives",
                Content = "Hello",
                ContentHash = "hash-1",
                SourceExternalId = "notion-page-1",
                SyncRunId = "run-1",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None
        );
        await Assert.That(upsertResult.Kind).IsEqualTo(DocumentUpsertKind.Added);

        // Simulate a Notion-side rename: same page id, new path, new content.
        var renamed = await libraryStore.UpsertSyncedDocumentAsync(
            new LibraryDocument
            {
                Id = SeedContext.NewId("doc"),
                OrganizationId = seed.Organization.Id,
                LibraryId = library.Id,
                Path = "engineering/2026-initiatives",
                Title = "2026 Initiatives",
                TitleNormalized = "2026 initiatives",
                Content = "Hello v2",
                ContentHash = "hash-2",
                SourceExternalId = "notion-page-1",
                SyncRunId = "run-2",
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None
        );

        await Assert.That(renamed.Kind).IsEqualTo(DocumentUpsertKind.Moved);
        await Assert.That(renamed.Document.Id).IsEqualTo(documentId);
        await Assert.That(renamed.Document.Path).IsEqualTo("engineering/2026-initiatives");

        var resolved = await libraryStore.GetByExternalIdAsync(
            seed.Organization.Id,
            library.Id,
            "notion-page-1",
            CancellationToken.None
        );

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Id).IsEqualTo(documentId);
        await Assert.That(resolved.Content).IsEqualTo("Hello v2");
    }
}
