using System.Data;
using Zeeq.Core.Carts;
using Zeeq.Data.Postgres.Carts;
using Zeeq.Testing;
using Zeeq.Testing.EntityGraphs;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.Tests;

/// <summary>
/// Integration tests for PostgresCartStore against a real test container.
/// Run:
/// dotnet run --project src/backend/Zeeq.Data.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/CartStoreIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class CartStoreIntegrationTests : PgTransactionalTestBase
{
    public CartStoreIntegrationTests(PgDatabaseFixture postgres)
        : base(postgres) { }

    [Test]
    public async Task CreateAsync_WithValidCart_PersistsRow()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCartStore(_context);
        var cart = TestCart(seed.Organization.Id, seed.Owner.Id);

        var created = await store.CreateAsync(cart, CancellationToken.None);
        _context.ChangeTracker.Clear();

        var found = await store.FindAsync(seed.Organization.Id, cart.Id, CancellationToken.None);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(cart.Id);
        await Assert.That(found.Name).IsEqualTo(cart.Name);
        await Assert.That(found.ItemSummaries.Count).IsEqualTo(1);
        await Assert.That(found.ItemsPayload.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAsync_AtCartLimit_ThrowsCartLimitExceededException()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCartStore(_context);

        // Seed 5 carts for the same owner.
        for (var i = 0; i < CartLimits.MaxCartsPerOwner; i++)
        {
            await store.CreateAsync(
                TestCart(seed.Organization.Id, seed.Owner.Id, id: $"cart_{i}"),
                CancellationToken.None
            );
        }

        _context.ChangeTracker.Clear();

        // 6th cart should throw.
        await Assert.ThrowsAsync<CartLimitExceededException>(() =>
            store.CreateAsync(
                TestCart(seed.Organization.Id, seed.Owner.Id, id: "cart_overflow"),
                CancellationToken.None
            )
        );
    }

    [Test]
    public async Task CreateAsync_WithDuplicateClientSuppliedId_ThrowsInvalidOperationException()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCartStore(_context);
        var cart = TestCart(seed.Organization.Id, seed.Owner.Id, id: "dup_id");

        await store.CreateAsync(cart, CancellationToken.None);
        _context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(cart, CancellationToken.None)
        );
    }

    [Test]
    public async Task ListForOwnerAsync_ReturnsOnlyCallersCartsOrderedByUpdatedAtDescending()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCartStore(_context);

        var cart1 = await store.CreateAsync(
            TestCart(seed.Organization.Id, seed.Owner.Id, id: "cart_1"),
            CancellationToken.None
        );

        await Task.Delay(10); // Ensure different SavedAtUtc

        var cart2 = await store.CreateAsync(
            TestCart(seed.Organization.Id, seed.Owner.Id, id: "cart_2"),
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();

        var results = await store.ListForOwnerAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            CancellationToken.None
        );

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0].Id).IsEqualTo(cart2.Id); // Newest first
        await Assert.That(results[1].Id).IsEqualTo(cart1.Id);
    }

    [Test]
    public async Task FindAsync_WithCartFromDifferentOrganization_ReturnsNull()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCartStore(_context);

        var cart = await store.CreateAsync(
            TestCart(seed.Organization.Id, seed.Owner.Id),
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();

        var found = await store.FindAsync("other_org", cart.Id, CancellationToken.None);

        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task DeleteAsync_RemovesRow_ReturnsFalseWhenAlreadyGone()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCartStore(_context);
        var cart = await store.CreateAsync(
            TestCart(seed.Organization.Id, seed.Owner.Id),
            CancellationToken.None
        );
        _context.ChangeTracker.Clear();

        var deleted = await store.DeleteAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            cart.Id,
            CancellationToken.None
        );

        await Assert.That(deleted).IsTrue();

        var deletedAgain = await store.DeleteAsync(
            seed.Organization.Id,
            seed.Owner.Id,
            cart.Id,
            CancellationToken.None
        );

        await Assert.That(deletedAgain).IsFalse();
    }

    [Test]
    public async Task Carts_Table_IsUnlogged()
    {
        var seed = await EntityGraph.AddGeneratedSeed(_context).BuildAsync();
        var store = new PostgresCartStore(_context);

        await store.CreateAsync(
            TestCart(seed.Organization.Id, seed.Owner.Id),
            CancellationToken.None
        );

        // Verify relpersistence = 'u' (unlogged) via pg_class.
        await using var cmd = _context.Database.GetDbConnection().CreateCommand();
        cmd.CommandText =
            "SELECT relpersistence = 'u' FROM pg_class WHERE relname = 'code_review_carts' AND relnamespace = 'zeeq'::regnamespace";

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {
            await cmd.Connection.OpenAsync();
        }

        var result = (bool)(await cmd.ExecuteScalarAsync())!;

        await Assert.That(result).IsTrue();
    }

    private static Cart TestCart(
        string organizationId,
        string ownerUserId,
        string? id = null,
        string? name = null
    )
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = id ?? $"cart_{Guid.CreateVersion7():N}",
            OrganizationId = organizationId,
            OwnerUserId = ownerUserId,
            Name = name ?? "swift-otter-jumps-a1b2",
            ItemSummaries =
            [
                new CartFindingSummary(
                    "abc123",
                    "Test finding",
                    "Security",
                    "A test finding summary",
                    "Critical",
                    "Test note"
                ),
            ],
            ItemsPayload =
            [
                new CartFindingSnapshot(
                    "abc123",
                    "Test finding",
                    "Critical",
                    "src/test.cs",
                    42,
                    "RIGHT",
                    "A test finding summary",
                    "Finding body text",
                    "owner/repo",
                    1,
                    "Security",
                    "Test Agent",
                    "Test note",
                    now
                ),
            ],
            CreatedAtUtc = now,
            SavedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }
}
