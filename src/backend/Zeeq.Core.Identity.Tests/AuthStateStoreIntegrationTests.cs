using System.Security.Claims;
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

    /// <summary>
    /// Verifies the inactive-organization flag survives the handoff serialization round-trip.
    /// </summary>
    /// <remarks>
    /// Regression guard: <c>AuthHandoff</c> is persisted as <c>SerializedAuthHandoff</c> JSON to
    /// cross the API-origin/frontend-origin boundary. A field missing from that record is
    /// silently dropped and reappears as its default, which sent users with an unactivated
    /// organization to their requested return URL instead of <c>/login?inactiveOrg=true</c>.
    /// </remarks>
    [Test]
    public async Task AuthHandoff_WithInactiveOrganization_SurvivesRoundTrip()
    {
        var handoffStore = new AuthHandoffStore(CreateStore());
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(AuthClaims.OrganizationId, "org_inactive")],
                authenticationType: "ExternalIdpCookie"
            )
        );

        var ticket = await handoffStore.StoreAsync(
            new AuthHandoff(
                Principal: principal,
                ReturnUrl: "/settings/github?tab=app",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(2),
                InactiveOrganization: true
            ),
            CancellationToken.None
        );

        var handoff = await handoffStore.ConsumeAsync(ticket, CancellationToken.None);

        await Assert.That(handoff).IsNotNull();
        await Assert.That(handoff!.InactiveOrganization).IsTrue();
        await Assert.That(handoff.ReturnUrl).IsEqualTo("/settings/github?tab=app");
    }

    /// <summary>
    /// Verifies an active-organization handoff still carries the caller's return URL.
    /// </summary>
    [Test]
    public async Task AuthHandoff_WithActiveOrganization_KeepsReturnUrl()
    {
        var handoffStore = new AuthHandoffStore(CreateStore());
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(AuthClaims.OrganizationId, "org_active")],
                authenticationType: "ExternalIdpCookie"
            )
        );

        var ticket = await handoffStore.StoreAsync(
            new AuthHandoff(
                Principal: principal,
                ReturnUrl: "/settings/github?tab=app",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(2)
            ),
            CancellationToken.None
        );

        var handoff = await handoffStore.ConsumeAsync(ticket, CancellationToken.None);

        await Assert.That(handoff).IsNotNull();
        await Assert.That(handoff!.InactiveOrganization).IsFalse();
        await Assert.That(handoff.ReturnUrl).IsEqualTo("/settings/github?tab=app");
    }

    private PostgresZeeqAuthStateStore CreateStore() => new(_context);
}
