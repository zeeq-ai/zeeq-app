using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Documents;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresExternalPendingContentSyncStore"/> — the dirty-content
/// set backing incremental externally-sourced ingest runs.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/PostgresExternalPendingContentSyncStoreTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresExternalPendingContentSyncStoreTests : PgTransactionalTestBase
{
    private static readonly TimeSpan DefaultStaleClaimAfter = TimeSpan.FromMinutes(15);

    public PostgresExternalPendingContentSyncStoreTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task PendingPageSync_UpsertTwiceForSamePage_ProducesOneRow()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );
        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.properties_updated",
            now.AddSeconds(1),
            CancellationToken.None
        );

        var claimed = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            DateTimeOffset.UtcNow,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        await Assert.That(claimed).HasSingleItem();
        await Assert.That(claimed[0].EventType).IsEqualTo("page.properties_updated");
    }

    [Test]
    public async Task PendingPageSync_UpsertAfterClaim_ResetsClaimAndSurvivesClear()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );
        var claimed = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );
        await Assert.That(claimed).HasSingleItem();

        // Page re-dirtied mid-run — this must reset the claim.
        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.moved",
            now.AddSeconds(5),
            CancellationToken.None
        );

        // Run-1's compare-and-delete must no-op: the claim no longer matches run-1.
        await store.ClearAsync(
            organizationId,
            libraryId,
            "page-1",
            "run-1",
            CancellationToken.None
        );

        var claimedAgain = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-2",
            now.AddSeconds(6),
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        await Assert.That(claimedAgain).HasSingleItem();
        await Assert.That(claimedAgain[0].EventType).IsEqualTo("page.moved");
    }

    [Test]
    public async Task PendingPageSync_ClaimWithConcurrentRuns_NeverDoubleClaims()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            await store.UpsertAsync(
                organizationId,
                libraryId,
                $"page-{i}",
                "page.content_updated",
                now,
                CancellationToken.None
            );
        }

        var claimed = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        await Assert.That(claimed.Count).IsEqualTo(5);

        // A second claim attempt against the same (now fully-claimed, none-yet-stale) set finds
        // nothing.
        //
        // NOTE: this exercises the claim predicate sequentially through one store/context, so it
        // does not exercise FOR UPDATE SKIP LOCKED under two genuinely overlapping transactions.
        // A true concurrency test needs two independent DbContexts with an open transaction held
        // on the first while the second claims — deferred; the predicate correctness (an already-
        // claimed, non-stale row is never reselected) is what this test actually verifies.
        var secondClaim = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-2",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        await Assert.That(secondClaim).IsEmpty();
    }

    [Test]
    public async Task PendingPageSync_ClearWithForeignRunId_DoesNotDelete()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );
        await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        // A stale/foreign run id must not be able to clear another run's claim.
        await store.ClearAsync(
            organizationId,
            libraryId,
            "page-1",
            "run-other",
            CancellationToken.None
        );

        var reclaimed = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-3",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        // Still claimed by run-1 and not yet stale, so run-3 sees nothing unclaimed.
        await Assert.That(reclaimed).IsEmpty();
    }

    [Test]
    public async Task PendingPageSync_Remove_DeletesRegardlessOfClaimOwner()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );
        await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        await store.RemoveAsync(organizationId, libraryId, "page-1", CancellationToken.None);

        var reclaimed = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-2",
            now + DefaultStaleClaimAfter + TimeSpan.FromSeconds(1),
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        await Assert.That(reclaimed).IsEmpty();
    }

    [Test]
    public async Task PendingPageSync_ClaimOlderThanStaleWindow_IsReclaimable()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );

        // run-1 claims the page and then, simulated here, crashes without clearing it.
        var firstClaim = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );
        await Assert.That(firstClaim).HasSingleItem();

        // A later claim attempt, once the lease window has elapsed, must be able to recover the
        // page — otherwise a crashed run strands it forever (no webhook redelivery guaranteed).
        var afterLeaseExpiry = now + DefaultStaleClaimAfter + TimeSpan.FromSeconds(1);
        var reclaimed = await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-2",
            afterLeaseExpiry,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        await Assert.That(reclaimed).HasSingleItem();
        await Assert.That(reclaimed[0].EventType).IsEqualTo("page.content_updated");
    }

    [Test]
    public async Task PendingPageSync_IsClaimActive_TrueForOwningRunId()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );
        await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        var active = await store.IsClaimActiveAsync(
            organizationId,
            libraryId,
            "page-1",
            "run-1",
            CancellationToken.None
        );

        await Assert.That(active).IsTrue();
    }

    [Test]
    public async Task PendingPageSync_IsClaimActive_FalseAfterDeleteWebhookRemovesRow()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );
        await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        // Simulates a page.deleted webhook racing the claimed run: it removes the row outright.
        await store.RemoveAsync(organizationId, libraryId, "page-1", CancellationToken.None);

        var active = await store.IsClaimActiveAsync(
            organizationId,
            libraryId,
            "page-1",
            "run-1",
            CancellationToken.None
        );

        await Assert.That(active).IsFalse();
    }

    [Test]
    public async Task PendingPageSync_IsClaimActive_FalseForForeignRunId()
    {
        var (store, organizationId, libraryId) = await CreateStoreAndLibraryAsync();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertAsync(
            organizationId,
            libraryId,
            "page-1",
            "page.content_updated",
            now,
            CancellationToken.None
        );
        await store.ClaimAsync(
            organizationId,
            libraryId,
            "run-1",
            now,
            DefaultStaleClaimAfter,
            CancellationToken.None
        );

        var active = await store.IsClaimActiveAsync(
            organizationId,
            libraryId,
            "page-1",
            "run-other",
            CancellationToken.None
        );

        await Assert.That(active).IsFalse();
    }

    private async Task<(
        PostgresExternalPendingContentSyncStore Store,
        string OrganizationId,
        string LibraryId
    )> CreateStoreAndLibraryAsync()
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

        return (
            new PostgresExternalPendingContentSyncStore(_context),
            seed.Organization.Id,
            library.Id
        );
    }
}
