using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresLibraryDocumentStore"/> delete
/// path, including the cascade-clean behavior that strips deleted library ids
/// from <see cref="CodeRepository.LibraryIds"/> arrays.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/PostgresLibraryDocumentStoreTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresLibraryDocumentStoreTests : PgTransactionalTestBase
{
    public PostgresLibraryDocumentStoreTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task UpdateLibraryAsync_PersistsIncludeAndExcludeFilters()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        var updated = await store.UpdateLibraryAsync(
            new Library
            {
                Id = library.Id,
                OrganizationId = organizationId,
                TeamId = library.TeamId,
                Name = library.Name,
                Description = library.Description,
                IncludeFilters = ["docs/**/*.md"],
                ExcludeFilters = ["docs/archive/**"],
                CreatedAt = library.CreatedAt,
                UpdatedAt = now,
            },
            CancellationToken.None
        );

        _context.ChangeTracker.Clear();
        var reloaded = await store.GetLibraryAsync(
            organizationId,
            library.Name,
            CancellationToken.None
        );

        await Assert.That(updated.IncludeFilters).Contains("docs/**/*.md");
        await Assert.That(updated.ExcludeFilters).Contains("docs/archive/**");
        await Assert.That(reloaded).IsNotNull();
        await Assert.That(reloaded!.IncludeFilters).Contains("docs/**/*.md");
        await Assert.That(reloaded.ExcludeFilters).Contains("docs/archive/**");
    }

    [Test]
    public async Task DeleteLibraryAsync_LibraryNotReferencedByAnyRepository_DeletesLibraryOnly()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();
        _context.ChangeTracker.Clear();

        await store.DeleteLibraryAsync(organizationId, library.Id, CancellationToken.None);

        var deleted = await store.GetLibraryAsync(
            organizationId,
            library.Name,
            CancellationToken.None
        );
        await Assert.That(deleted).IsNull();
    }

    [Test]
    public async Task DeleteLibraryAsync_LibraryReferencedByOneRepository_RemovesIdFromLibraryIdsAndDeletesLibrary()
    {
        var libraryId = SeedContext.NewId("library");
        var (seed, repository) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(proto => proto.LibraryIds = [libraryId])
            .BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var library = await CreateLibraryWithIdAsync(
            store,
            seed.Organization.Id,
            "test-lib",
            libraryId
        );
        _context.ChangeTracker.Clear();

        await store.DeleteLibraryAsync(seed.Organization.Id, library.Id, CancellationToken.None);

        var deleted = await store.GetLibraryAsync(
            seed.Organization.Id,
            library.Name,
            CancellationToken.None
        );
        await Assert.That(deleted).IsNull();

        var reloadedRepo = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == repository.Id);
        await Assert.That(reloadedRepo.LibraryIds).DoesNotContain(library.Id);
    }

    [Test]
    public async Task DeleteLibraryAsync_LibraryReferencedByMultipleRepositories_CleansAllOfThem()
    {
        var libraryId = SeedContext.NewId("library");
        var unrelatedLibraryId = SeedContext.NewId("library");
        var (seed, repo1, repo2) = await EntityGraph
            .AddGeneratedSeed(_context)
            .AddCodeRepository(proto => proto.LibraryIds = [libraryId, unrelatedLibraryId])
            .AddCodeRepository(proto => proto.LibraryIds = [libraryId])
            .BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var library = await CreateLibraryWithIdAsync(
            store,
            seed.Organization.Id,
            "test-lib",
            libraryId
        );
        _context.ChangeTracker.Clear();

        await store.DeleteLibraryAsync(seed.Organization.Id, library.Id, CancellationToken.None);

        var reloadedRepo1 = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == repo1.Id);
        var reloadedRepo2 = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == repo2.Id);

        // The deleted library id is stripped from both repositories.
        await Assert.That(reloadedRepo1.LibraryIds).DoesNotContain(library.Id);
        await Assert.That(reloadedRepo2.LibraryIds).DoesNotContain(library.Id);

        // Unrelated library ids are preserved.
        await Assert.That(reloadedRepo1.LibraryIds).Contains(unrelatedLibraryId);
    }

    [Test]
    public async Task DeleteLibraryAsync_RepositoryInDifferentOrganization_IsNotTouched()
    {
        var (store, organizationId, library) = await CreateStoreWithLibraryAsync();

        // Create a repository in a different organization with the same library id string.
        var otherSeed = SeedContext.Generate();
        var otherRepo = await EntityGraph
            .SeedWith(otherSeed, _context)
            .AddCodeRepository(proto => proto.LibraryIds = [library.Id])
            .BuildAsync();
        _context.ChangeTracker.Clear();

        await store.DeleteLibraryAsync(organizationId, library.Id, CancellationToken.None);

        // The repository in the other organization is untouched.
        var reloadedOtherRepo = await _context
            .CodeRepositories.AsNoTracking()
            .SingleAsync(r => r.Id == otherRepo.Id);
        await Assert.That(reloadedOtherRepo.LibraryIds).Contains(library.Id);
    }

    [Test]
    public async Task DeleteLibraryAsync_LibraryDoesNotExist_NoOp()
    {
        var (store, organizationId, _) = await CreateStoreWithLibraryAsync();
        var nonExistentId = SeedContext.NewId("library");
        _context.ChangeTracker.Clear();

        // Should not throw.
        await store.DeleteLibraryAsync(organizationId, nonExistentId, CancellationToken.None);
    }

    [Test]
    public async Task ResetLibrarySyncStateAsync_ActivePrivateLibrary_ClearsLeaseAndPreservesManualHistory()
    {
        var (store, organizationId, library) = await CreateStoreWithPrivateLibraryAsync();
        var triggerAt = DateTimeOffset.UtcNow.AddMinutes(-5).TruncateToPostgresPrecision();
        var runCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2).TruncateToPostgresPrecision();
        await store.UpdateSyncLeaseAsync(
            organizationId,
            library.Id,
            syncStatus: "running",
            nextSyncAt: DateTimeOffset.UtcNow.AddHours(1),
            manualTriggerHistory: [triggerAt],
            sourceSyncedAt: library.SourceSyncedAt,
            activeSyncRunId: "run_stalled",
            activeSyncRunCreatedAtUtc: runCreatedAt,
            syncQueuedAtUtc: runCreatedAt,
            syncStartedAtUtc: runCreatedAt,
            ct: CancellationToken.None
        );
        _context.DocsIngestRuns.Add(
            new DocsIngestRun
            {
                Id = "run_stalled",
                CreatedAtUtc = runCreatedAt,
                SourceKind = RepositorySourceKind.Private,
                RepoUrl = library.SourceRepoUrl!,
                OrganizationId = organizationId,
                LibraryId = library.Id,
                Trigger = IngestTriggerReason.Manual,
                Status = IngestRunStatus.Running,
                StartedAtUtc = runCreatedAt,
                UpdatedAtUtc = runCreatedAt,
            }
        );
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var now = DateTimeOffset.UtcNow;
        var reset = await store.ResetLibrarySyncStateAsync(
            organizationId,
            library.Id,
            now,
            CancellationToken.None
        );

        await Assert.That(reset).IsNotNull();
        await Assert.That(reset!.ClearedSync.RunId).IsEqualTo("run_stalled");
        await Assert.That(reset.Library.SyncStatus).IsEqualTo("idle");
        await Assert.That(reset.Library.NextSyncAt).IsEqualTo(now.TruncateToPostgresPrecision());
        await Assert.That(reset.Library.ManualTriggerHistory).Contains(triggerAt);
        await Assert.That(reset.Library.ActiveSyncRunId).IsNull();
        await Assert.That(reset.Library.SyncQueuedAtUtc).IsNull();
        await Assert.That(reset.Library.SyncStartedAtUtc).IsNull();
        var run = await _context.DocsIngestRuns.SingleAsync(row => row.Id == "run_stalled");
        await Assert.That(run.Status).IsEqualTo(IngestRunStatus.Stalled);
    }

    [Test]
    public async Task ResetLibrarySyncStateAsync_MissingActiveRun_LeavesLeaseIntact()
    {
        var (store, organizationId, library) = await CreateStoreWithPrivateLibraryAsync();
        var runCreatedAt = DateTimeOffset.UtcNow.AddHours(-3);
        await store.UpdateSyncLeaseAsync(
            organizationId,
            library.Id,
            syncStatus: "running",
            nextSyncAt: DateTimeOffset.UtcNow.AddHours(1),
            manualTriggerHistory: [],
            sourceSyncedAt: library.SourceSyncedAt,
            activeSyncRunId: "run_missing",
            activeSyncRunCreatedAtUtc: runCreatedAt,
            syncQueuedAtUtc: runCreatedAt,
            syncStartedAtUtc: runCreatedAt,
            ct: CancellationToken.None
        );
        _context.ChangeTracker.Clear();

        var reset = await store.ResetLibrarySyncStateAsync(
            organizationId,
            library.Id,
            DateTimeOffset.UtcNow,
            CancellationToken.None
        );

        await Assert.That(reset).IsNull();

        _context.ChangeTracker.Clear();
        var reloaded = await store.GetLibraryByIdAsync(
            organizationId,
            library.Id,
            CancellationToken.None
        );
        await Assert.That(reloaded!.SyncStatus).IsEqualTo("running");
        await Assert.That(reloaded.ActiveSyncRunId).IsEqualTo("run_missing");
    }

    [Test]
    public async Task ClaimDueForSyncAsync_DuePrivateLibrary_UsesPostgresPrecisionRunTimestamp()
    {
        var (store, organizationId, library) = await CreateStoreWithPrivateLibraryAsync();
        library.SyncStatus = "idle";
        library.NextSyncAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var claimed = await store.ClaimDueForSyncAsync(10, CancellationToken.None);

        var claimedLibrary = claimed.Single(row =>
            row.OrganizationId == organizationId && row.Id == library.Id
        );
        await Assert.That(claimedLibrary.ActiveSyncRunCreatedAtUtc).IsNotNull();
        await AssertPostgresMicrosecondPrecisionAsync(
            claimedLibrary.ActiveSyncRunCreatedAtUtc.Value
        );
        await Assert.That(claimedLibrary.SyncQueuedAtUtc).IsNotNull();
        await AssertPostgresMicrosecondPrecisionAsync(claimedLibrary.SyncQueuedAtUtc.Value);
    }

    private async Task<(
        PostgresLibraryDocumentStore Store,
        string OrganizationId,
        Library Library
    )> CreateStoreWithLibraryAsync()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var library = await CreateLibraryAsync(store, seed.Organization.Id, "test-lib");

        return (store, seed.Organization.Id, library);
    }

    private async Task<(
        PostgresLibraryDocumentStore Store,
        string OrganizationId,
        Library Library
    )> CreateStoreWithPrivateLibraryAsync()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresLibraryDocumentStore(_context, new DocumentSearchScope());
        var now = DateTimeOffset.UtcNow;
        var library = await store.CreateLibraryAsync(
            new Library
            {
                Id = SeedContext.NewId("library"),
                OrganizationId = seed.Organization.Id,
                Name = "private-lib",
                SourceKind = "GitHub",
                SourceRepoUrl = "https://github.com/acme/private-lib",
                SyncStatus = "idle",
                NextSyncAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None
        );

        return (store, seed.Organization.Id, library);
    }

    private static Task<Library> CreateLibraryAsync(
        PostgresLibraryDocumentStore store,
        string organizationId,
        string name
    ) => CreateLibraryWithIdAsync(store, organizationId, name, SeedContext.NewId("library"));

    private static Task<Library> CreateLibraryWithIdAsync(
        PostgresLibraryDocumentStore store,
        string organizationId,
        string name,
        string libraryId
    )
    {
        var now = DateTimeOffset.UtcNow;
        return store.CreateLibraryAsync(
            new Library
            {
                Id = libraryId,
                OrganizationId = organizationId,
                Name = name,
                CreatedAt = now,
                UpdatedAt = now,
            },
            default
        );
    }

    private static async Task AssertPostgresMicrosecondPrecisionAsync(DateTimeOffset value) =>
        await Assert.That(value.Ticks % TimeSpan.TicksPerMicrosecond).IsEqualTo(0);
}
