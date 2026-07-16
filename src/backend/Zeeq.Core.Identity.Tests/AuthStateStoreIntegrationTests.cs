using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;

namespace Zeeq.Core.Identity.Tests;

[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public class AuthStateStoreIntegrationTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task ConsumeAsync_WithStoredState_ReturnsPayloadOnlyOnce()
    {
        var store = CreateStore();
        await store.StoreAsync(
            "oauth_state",
            "state_123",
            "{\"value\":123}",
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None
        );

        var first = await store.ConsumeAsync("oauth_state", "state_123", CancellationToken.None);
        var second = await store.ConsumeAsync("oauth_state", "state_123", CancellationToken.None);

        await Assert.That(first).IsEqualTo("{\"value\":123}");
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task ConsumeAsync_WithWrongPurpose_ReturnsNull()
    {
        var store = CreateStore();
        await store.StoreAsync(
            "oauth_state",
            "state_wrong_purpose",
            "payload",
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None
        );

        var payload = await store.ConsumeAsync(
            "auth_handoff",
            "state_wrong_purpose",
            CancellationToken.None
        );

        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task ConsumeAsync_WithExpiredState_ReturnsNull()
    {
        var store = CreateStore();
        await store.StoreAsync(
            "oauth_state",
            "state_expired",
            "payload",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None
        );

        var payload = await store.ConsumeAsync(
            "oauth_state",
            "state_expired",
            CancellationToken.None
        );

        await Assert.That(payload).IsNull();
    }

    private PostgresZeeqAuthStateStore CreateStore() => new(_context);
}
