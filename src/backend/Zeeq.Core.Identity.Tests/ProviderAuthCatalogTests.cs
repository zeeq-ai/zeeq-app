using Zeeq.Core.Common;

namespace Zeeq.Core.Identity.Tests;

public sealed class ProviderAuthCatalogTests
{
    [Test]
    public async Task Create_DefaultSettings_0FactorDevelopmentContract()
    {
        var provider =
            ProviderAuthCatalog.Create(new AuthSettings()).Find("mock")
            ?? throw new InvalidOperationException("The development mock provider is missing.");

        await Assert.That(provider.Name).IsEqualTo("mock");
        await Assert.That(provider.DisplayName).IsEqualTo("0factor");
        await Assert.That(provider.IssuerUri).IsEqualTo("http://localhost:9321/realms/zeeq");
        await Assert.That(provider.ClientId).IsEqualTo("mcp-openiddict-local");
        await Assert
            .That(provider.ServerCallbackUri)
            .IsEqualTo("http://localhost:8096/auth/callback/mock");
        await Assert.That(string.Join(' ', provider.Scopes)).IsEqualTo("openid profile email");
    }

    [Test]
    public async Task Create_WithoutMockProvider_DoesNotExposeMockLogin()
    {
        var catalog = ProviderAuthCatalog.Create(new AuthSettings(), includeMock: false);

        await Assert.That(catalog.Find("mock")).IsNull();
    }
}
