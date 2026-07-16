using Zeeq.Core.Models;
using Zeeq.Data.Postgres.CodeReviews;
using Zeeq.Platform.CodeReviews;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for the transient GitHub comment lease table and store.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubCommentLeaseStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class GitHubCommentLeaseStoreIntegrationTests : PgTransactionalTestBase
{
    private readonly PgDatabaseFixture _postgres;

    public GitHubCommentLeaseStoreIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres)
    {
        _postgres = postgres;
    }

    [Test]
    public async Task LeaseTable_IsUnlogged()
    {
        await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relpersistence
            FROM pg_class table_class
            JOIN pg_namespace table_schema ON table_schema.oid = table_class.relnamespace
            WHERE table_schema.nspname = 'zeeq'
              AND table_class.relname = 'code_review_github_comment_leases'
            """;

        var persistence = Convert.ToString(await command.ExecuteScalarAsync());

        await Assert.That(persistence).IsEqualTo("u");
    }

    [Test]
    public async Task TryAcquireAsync_ReturnsTrueWhenLeaseDoesNotExist()
    {
        var key = NewLeaseKey();
        await using var context = _postgres.CreateContext();
        var store = CreateStore(context);

        var acquired = await store.TryAcquireAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );
        var lease = await FindLeaseAsync(key);

        await Assert.That(acquired).IsTrue();
        await Assert.That(lease).IsNotNull();
        await Assert.That(lease!.WorkerId).IsEqualTo("worker-a");
        await Assert.That(lease.ExpiresAtUtc).IsGreaterThan(lease.AcquiredAtUtc);
    }

    [Test]
    public async Task TryAcquireAsync_ReturnsFalseWhenLeaseIsStillLive()
    {
        var key = NewLeaseKey();
        await using var context = _postgres.CreateContext();
        var store = CreateStore(context);

        var first = await store.TryAcquireAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );
        var second = await CreateStore(context)
            .TryAcquireAsync(
                key,
                workerId: "worker-b",
                TimeSpan.FromMinutes(1),
                CancellationToken.None
            );
        var lease = await FindLeaseAsync(key);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(lease!.WorkerId).IsEqualTo("worker-a");
    }

    [Test]
    public async Task TryAcquireAsync_TakesOverExpiredLease()
    {
        var key = NewLeaseKey();
        await using var seedContext = _postgres.CreateContext();
        seedContext.GitHubCommentLeases.Add(
            new GitHubCommentLease
            {
                LeaseKey = key.ToString(),
                WorkerId = "worker-a",
                AcquiredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            }
        );
        await seedContext.SaveChangesAsync();

        await using var context = _postgres.CreateContext();
        var store = CreateStore(context);

        var acquired = await store.TryAcquireAsync(
            key,
            workerId: "worker-b",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );
        var lease = await FindLeaseAsync(key);

        await Assert.That(acquired).IsTrue();
        await Assert.That(lease!.WorkerId).IsEqualTo("worker-b");
        await Assert.That(lease.ExpiresAtUtc).IsGreaterThan(DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task RenewAsync_ExtendsLeaseForCurrentWorker()
    {
        var key = NewLeaseKey();
        await using var context = _postgres.CreateContext();
        var store = CreateStore(context);
        await store.TryAcquireAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromSeconds(10),
            CancellationToken.None
        );
        var originalExpiresAt = (await FindLeaseAsync(key))!.ExpiresAtUtc;

        var renewed = await store.RenewAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        var lease = await FindLeaseAsync(key);

        await Assert.That(renewed).IsTrue();
        await Assert.That(lease!.ExpiresAtUtc).IsGreaterThan(originalExpiresAt);
    }

    [Test]
    public async Task RenewAsync_ReturnsFalseForWrongWorker()
    {
        var key = NewLeaseKey();
        await using var context = _postgres.CreateContext();
        var store = CreateStore(context);
        await store.TryAcquireAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );
        var originalExpiresAt = (await FindLeaseAsync(key))!.ExpiresAtUtc;

        var renewed = await store.RenewAsync(
            key,
            workerId: "worker-b",
            TimeSpan.FromMinutes(5),
            CancellationToken.None
        );
        var lease = await FindLeaseAsync(key);

        await Assert.That(renewed).IsFalse();
        await Assert.That(lease!.ExpiresAtUtc).IsEqualTo(originalExpiresAt);
    }

    [Test]
    public async Task ReleaseAsync_RemovesLeaseForCurrentWorker()
    {
        var key = NewLeaseKey();
        await using var context = _postgres.CreateContext();
        var store = CreateStore(context);
        await store.TryAcquireAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );

        await store.ReleaseAsync(key, workerId: "worker-a", CancellationToken.None);
        var lease = await FindLeaseAsync(key);

        await Assert.That(lease).IsNull();
    }

    [Test]
    public async Task TryAcquireAsync_SeesReleaseAfterFailedAttemptOnSameStore()
    {
        var key = NewLeaseKey();
        await using var firstContext = _postgres.CreateContext();
        await using var waitingContext = _postgres.CreateContext();
        var firstStore = CreateStore(firstContext);
        var waitingStore = CreateStore(waitingContext);

        var first = await firstStore.TryAcquireAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );
        var blocked = await waitingStore.TryAcquireAsync(
            key,
            workerId: "worker-b",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );

        await firstStore.ReleaseAsync(key, workerId: "worker-a", CancellationToken.None);

        var acquiredAfterRelease = await waitingStore.TryAcquireAsync(
            key,
            workerId: "worker-b",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );
        var lease = await FindLeaseAsync(key);

        await Assert.That(first).IsTrue();
        await Assert.That(blocked).IsFalse();
        await Assert.That(acquiredAfterRelease).IsTrue();
        await Assert.That(lease!.WorkerId).IsEqualTo("worker-b");
    }

    [Test]
    public async Task ReleaseAsync_DoesNotRemoveLeaseForWrongWorker()
    {
        var key = NewLeaseKey();
        await using var context = _postgres.CreateContext();
        var store = CreateStore(context);
        await store.TryAcquireAsync(
            key,
            workerId: "worker-a",
            TimeSpan.FromMinutes(1),
            CancellationToken.None
        );

        await store.ReleaseAsync(key, workerId: "worker-b", CancellationToken.None);
        var lease = await FindLeaseAsync(key);

        await Assert.That(lease).IsNotNull();
        await Assert.That(lease!.WorkerId).IsEqualTo("worker-a");
    }

    [Test]
    public async Task TryAcquireAsync_AllowsOnlyOneConcurrentOwnerForSameKey()
    {
        var key = NewLeaseKey();
        var firstWorker = SeedContext.NewId("worker");
        var secondWorker = SeedContext.NewId("worker");

        await using var firstContext = _postgres.CreateContext();
        await using var secondContext = _postgres.CreateContext();
        var firstStore = CreateStore(firstContext);
        var secondStore = CreateStore(secondContext);

        var results = await Task.WhenAll(
            firstStore.TryAcquireAsync(
                key,
                firstWorker,
                TimeSpan.FromMinutes(1),
                CancellationToken.None
            ),
            secondStore.TryAcquireAsync(
                key,
                secondWorker,
                TimeSpan.FromMinutes(1),
                CancellationToken.None
            )
        );

        try
        {
            await Assert.That(results.Count(result => result)).IsEqualTo(1);
        }
        finally
        {
            // This test intentionally commits through separate contexts so the
            // concurrent acquire observes real database behavior. Clean up the
            // unique lease key regardless of which worker won.
            await firstStore.ReleaseAsync(key, firstWorker, CancellationToken.None);
            await secondStore.ReleaseAsync(key, secondWorker, CancellationToken.None);
        }
    }

    private PostgresGitHubCommentLeaseStore CreateStore(PostgresDbContext db) =>
        new(db, new FixtureDbContextFactory(_postgres));

    private async Task<GitHubCommentLease?> FindLeaseAsync(GitHubCommentLeaseKey key)
    {
        await using var context = _postgres.CreateContext();

        return await context
            .GitHubCommentLeases.AsNoTracking()
            .SingleOrDefaultAsync(lease => lease.LeaseKey == key.ToString());
    }

    private static GitHubCommentLeaseKey NewLeaseKey() =>
        new(
            new GitHubCommentTargetSelector(
                OrganizationId: SeedContext.NewId("org"),
                RepositoryId: SeedContext.NewId("repo"),
                PullRequestNumber: Random.Shared.Next(1, 10_000),
                Kind: GitHubCommentTargetKind.PullRequestSummary,
                ScopeKey: "summary"
            )
        );

    private sealed class FixtureDbContextFactory(PgDatabaseFixture postgres)
        : IDbContextFactory<PostgresDbContext>
    {
        public PostgresDbContext CreateDbContext() => postgres.CreateContext();
    }
}
