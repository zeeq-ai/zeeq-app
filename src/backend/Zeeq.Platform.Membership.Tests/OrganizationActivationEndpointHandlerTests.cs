using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Membership.Tests;

public sealed class OrganizationActivationEndpointHandlerTests
{
    [Test]
    public async Task Exchange_WithValidSessionAndKey_ActivatesOrganization()
    {
        // Guards that the authenticated exchange endpoint passes only the key
        // hash plus session user/org identity to the activation store.
        var store = new TestOrganizationActivationKeyStore
        {
            ExchangeResult = OrganizationActivationExchangeResult.Activated,
        };
        var rawKey = OrganizationActivationKeyMaterial.GenerateKey();
        var handler = new ExchangeOrganizationActivationKeyHandler(
            store,
            EnabledSettings(),
            CreateCache()
        );

        var result = await handler.HandleAsync(
            new OrganizationActivationExchangeRequest(rawKey),
            TestUser("user_1", "org_1"),
            CancellationToken.None
        );

        var ok = result.Result as Ok<OrganizationActivationExchangeResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.OrganizationId).IsEqualTo("org_1");
        await Assert
            .That(store.LastKeyHash)
            .IsEqualTo(OrganizationActivationKeyMaterial.ComputeHash(rawKey));
        await Assert.That(store.LastOrganizationId).IsEqualTo("org_1");
        await Assert.That(store.LastUserId).IsEqualTo("user_1");
    }

    [Test]
    public async Task SystemActivationKeyHandlers_Disabled_ReturnNotFoundWithoutStoreAccess()
    {
        // Guards that admin activation-key routes can stay mapped for client
        // generation while disabled deployments behave as if the feature is absent.
        var store = new TestOrganizationActivationKeyStore();
        var settings = DisabledSettings();

        var listResult = await new ListSystemActivationKeysHandler(store, settings).HandleAsync(
            1,
            25,
            null,
            null,
            CancellationToken.None
        );
        var createResult = await new CreateSystemActivationKeyHandler(store, settings).HandleAsync(
            new CreateSystemActivationKeyRequest(),
            TestUser("admin_1", "org_1"),
            CancellationToken.None
        );
        var revokeResult = await new RevokeSystemActivationKeyHandler(store, settings).HandleAsync(
            "oak_1",
            TestUser("admin_1", "org_1"),
            CancellationToken.None
        );

        await Assert.That(listResult.Result is NotFound).IsTrue();
        await Assert.That(createResult.Result is NotFound).IsTrue();
        await Assert.That(revokeResult.Result is NotFound).IsTrue();
        await Assert.That(store.StoreAccessCount).IsEqualTo(0);
    }

    [Test]
    public async Task PlatformSettings_InvalidActivationKeyLifetimeBounds_RejectsAboveSupportedMaximum()
    {
        // Guards that deployment configuration cannot accept a lifetime that
        // would later overflow DateTimeOffset.AddDays during key creation.
        var settings = new PlatformSettings
        {
            OrganizationActivationKeyDefaultLifetimeDays = 90,
            OrganizationActivationKeyMaxLifetimeDays =
                PlatformSettings.MaxSupportedOrganizationActivationKeyLifetimeDays + 1,
        };

        await Assert.That(settings.HasValidOrganizationActivationKeyLifetimeBounds()).IsFalse();
    }

    [Test]
    public async Task CreateSystemActivationKeyHandler_ExpiresInDaysAboveSupportedMax_ReturnsValidationProblem()
    {
        // Guards that direct handler execution rejects unsupported lifetimes
        // before date arithmetic or key persistence can run.
        var store = new TestOrganizationActivationKeyStore();
        var settings = new AppSettings
        {
            Platform = new PlatformSettings
            {
                OrganizationActivationKeysEnabled = true,
                OrganizationActivationKeyMaxLifetimeDays =
                    PlatformSettings.MaxSupportedOrganizationActivationKeyLifetimeDays,
            },
        };
        var handler = new CreateSystemActivationKeyHandler(store, settings);

        var result = await handler.HandleAsync(
            new CreateSystemActivationKeyRequest
            {
                ExpiresInDays =
                    PlatformSettings.MaxSupportedOrganizationActivationKeyLifetimeDays + 1,
            },
            TestUser("admin_1", "org_1"),
            CancellationToken.None
        );

        await Assert.That(result.Result is ValidationProblem).IsTrue();
        await Assert.That(store.StoreAccessCount).IsEqualTo(0);
    }

    private static AppSettings DisabledSettings() => new();

    private static AppSettings EnabledSettings() =>
        new() { Platform = new PlatformSettings { OrganizationActivationKeysEnabled = true } };

    private static HybridCache CreateCache() =>
        CreateServiceProvider().GetRequiredService<HybridCache>();

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();

        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal TestUser(string userId, string organizationId)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.Subject, userId),
                new Claim(AuthClaims.OrganizationId, organizationId),
            ],
            "test"
        );

        return new ClaimsPrincipal(identity);
    }

    private sealed class TestOrganizationActivationKeyStore : IOrganizationActivationKeyStore
    {
        public OrganizationActivationExchangeResult ExchangeResult { get; init; } =
            OrganizationActivationExchangeResult.InvalidKey;

        public string? LastKeyHash { get; private set; }

        public string? LastOrganizationId { get; private set; }

        public string? LastUserId { get; private set; }

        public int StoreAccessCount { get; private set; }

        public Task<OrganizationActivationKey> CreateKeyAsync(
            OrganizationActivationKey key,
            CancellationToken ct
        )
        {
            StoreAccessCount++;
            return Task.FromResult(key);
        }

        public Task<OrganizationActivationKeyPage<OrganizationActivationKeySummary>> ListKeysAsync(
            int page,
            int pageSize,
            string? query,
            OrganizationActivationKeyStatus? status,
            CancellationToken ct
        )
        {
            StoreAccessCount++;
            return Task.FromResult(
                new OrganizationActivationKeyPage<OrganizationActivationKeySummary>(
                    [],
                    page,
                    pageSize,
                    0
                )
            );
        }

        public Task<OrganizationActivationKeySummary?> RevokeKeyAsync(
            string keyId,
            CancellationToken ct
        )
        {
            StoreAccessCount++;
            return Task.FromResult<OrganizationActivationKeySummary?>(null);
        }

        public Task<OrganizationActivationExchangeResult> ConsumeKeyAndActivateOrganizationAsync(
            string keyHash,
            string organizationId,
            string userId,
            CancellationToken ct
        )
        {
            StoreAccessCount++;
            LastKeyHash = keyHash;
            LastOrganizationId = organizationId;
            LastUserId = userId;
            return Task.FromResult(ExchangeResult);
        }
    }
}
