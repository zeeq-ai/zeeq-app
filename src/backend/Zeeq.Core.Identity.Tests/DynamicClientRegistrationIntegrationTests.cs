using System.Security.Claims;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Identity;
using Zeeq.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity.Tests;

[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public class DynamicClientRegistrationSetupTests(PgDatabaseFixture postgres)
    : PgTransactionalTestBase(postgres)
{
    [Test]
    public async Task ClaimOrValidateActiveAsync_WithPendingSetup_ClaimsAuthenticatedUser()
    {
        var context = await EnsureAuthContextAsync("mock", "123");
        var setup = CreatePendingSetup("mcp_pending_claim");
        _context.DcrClientSetups.Add(setup);
        await _context.SaveChangesAsync();

        var decision = await CreateService()
            .ClaimOrValidateActiveAsync(
                setup.ClientId,
                CreateUser(context, "mock", "123"),
                CancellationToken.None
            );

        _context.ChangeTracker.Clear();
        var updated = await _context.DcrClientSetups.SingleAsync(item =>
            item.ClientId == setup.ClientId
        );

        await Assert.That(decision.Succeeded).IsTrue();
        await Assert.That(updated.Status).IsEqualTo(DcrClientSetup.Active);
        await Assert.That(updated.ClaimedUserId).IsEqualTo(context.UserId);
        await Assert.That(updated.OrganizationId).IsEqualTo(context.OrganizationId);
        await Assert.That(updated.TeamId).IsEqualTo(context.TeamId);
        await Assert.That(updated.ClaimedOwnerProvider).IsEqualTo("mock");
        await Assert.That(updated.ClaimedOwnerProviderSubject).IsEqualTo("123");
        await Assert.That(updated.ClaimedAtUtc).IsNotNull();
    }

    [Test]
    public async Task ClaimOrValidateActiveAsync_WithActiveSetup_AllowsSameUser()
    {
        var context = await EnsureAuthContextAsync("mock", "123");
        var setup = CreateActiveSetup("mcp_active_same", context, "mock", "123");
        _context.DcrClientSetups.Add(setup);
        await _context.SaveChangesAsync();

        var decision = await CreateService()
            .ClaimOrValidateActiveAsync(
                setup.ClientId,
                CreateUser(context, "mock", "123"),
                CancellationToken.None
            );

        await Assert.That(decision.Succeeded).IsTrue();
    }

    [Test]
    public async Task ClaimOrValidateActiveAsync_WithActiveSetup_RejectsDifferentUser()
    {
        var owner = await EnsureAuthContextAsync("mock", "123");
        var other = await EnsureAuthContextAsync("mock", "456");
        var setup = CreateActiveSetup("mcp_active_other", owner, "mock", "123");
        _context.DcrClientSetups.Add(setup);
        await _context.SaveChangesAsync();

        var decision = await CreateService()
            .ClaimOrValidateActiveAsync(
                setup.ClientId,
                CreateUser(other, "mock", "456"),
                CancellationToken.None
            );

        await Assert.That(decision.Succeeded).IsFalse();
        await Assert.That(decision.Error).IsEqualTo(OpenIddictConstants.Errors.AccessDenied);
    }

    [Test]
    public async Task ClaimOrValidateActiveAsync_WithExpiredPendingSetup_RejectsAndMarksExpired()
    {
        var context = await EnsureAuthContextAsync("mock", "123");
        var setup = CreatePendingSetup(
            "mcp_expired_pending",
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)
        );
        _context.DcrClientSetups.Add(setup);
        await _context.SaveChangesAsync();

        var decision = await CreateService()
            .ClaimOrValidateActiveAsync(
                setup.ClientId,
                CreateUser(context, "mock", "123"),
                CancellationToken.None
            );

        _context.ChangeTracker.Clear();
        var updated = await _context.DcrClientSetups.SingleAsync(item =>
            item.ClientId == setup.ClientId
        );

        await Assert.That(decision.Succeeded).IsFalse();
        await Assert.That(updated.Status).IsEqualTo(DcrClientSetup.Expired);
    }

    [Test]
    public async Task ValidateActiveForTokenExchangeAsync_WithPendingSetup_RejectsTokenExchange()
    {
        var setup = CreatePendingSetup("mcp_pending_token");
        _context.DcrClientSetups.Add(setup);
        await _context.SaveChangesAsync();

        var decision = await CreateService()
            .ValidateActiveForTokenExchangeAsync(setup.ClientId, CancellationToken.None);

        await Assert.That(decision.Succeeded).IsFalse();
        await Assert.That(decision.Error).IsEqualTo(OpenIddictConstants.Errors.InvalidGrant);
    }

    private DcrClientSetupService CreateService() =>
        new(CreateIdentityStore(), NullLogger<DcrClientSetupService>.Instance);

    private PostgresZeeqIdentityStore CreateIdentityStore() => new(_context);

    private async Task<AuthContext> EnsureAuthContextAsync(string provider, string subject)
    {
        var context = await CreateIdentityStore()
            .EnsureUserAsync(
                provider,
                subject,
                "Test User " + subject,
                null,
                null,
                CancellationToken.None
            );
        _context.ChangeTracker.Clear();

        return context;
    }

    private static ClaimsPrincipal CreateUser(
        AuthContext context,
        string provider,
        string providerSubject
    ) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(Claims.Subject, context.UserId),
                    new Claim(AuthClaims.OrganizationId, context.OrganizationId),
                    new Claim(AuthClaims.TeamId, context.TeamId),
                    new Claim(AuthClaims.PartitionIds, "[]"),
                    new Claim(AuthClaims.Provider, provider),
                    new Claim(AuthClaims.ProviderSubject, providerSubject),
                ],
                authenticationType: "Test"
            )
        );

    private static DcrClientSetup CreatePendingSetup(
        string clientId,
        DateTimeOffset? expiresAtUtc = null
    ) =>
        new()
        {
            ClientId = clientId,
            Status = DcrClientSetup.PendingLogin,
            ClientName = "Test MCP Client",
            RedirectUrisJson = "[\"http://127.0.0.1:3456/callback\"]",
            RequestedScopes = "openid profile email mcp:tools",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = expiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(15),
        };

    private static DcrClientSetup CreateActiveSetup(
        string clientId,
        AuthContext context,
        string provider,
        string providerSubject
    ) =>
        new()
        {
            ClientId = clientId,
            Status = DcrClientSetup.Active,
            ClientName = "Test MCP Client",
            RedirectUrisJson = "[\"http://127.0.0.1:3456/callback\"]",
            RequestedScopes = "openid profile email mcp:tools",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
            ClaimedAtUtc = DateTimeOffset.UtcNow,
            ClaimedUserId = context.UserId,
            OrganizationId = context.OrganizationId,
            TeamId = context.TeamId,
            SelectedPartitionIdsJson = "[]",
            ClaimedOwnerProvider = provider,
            ClaimedOwnerProviderSubject = providerSubject,
        };
}
