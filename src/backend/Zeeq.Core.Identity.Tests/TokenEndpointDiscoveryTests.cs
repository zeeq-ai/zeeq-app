using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Zeeq.Core.Common;

namespace Zeeq.Core.Identity.Tests;

public sealed class TokenEndpointDiscoveryTests
{
    [Test]
    [Arguments("Development")]
    [Arguments("Production")]
    public async Task Default_PreservesIssuerEndpoint(string environment)
    {
        var settings = new AuthSettings { Issuer = "https://app.example.test/" };

        var endpoint = SetupIdentityExtension.ResolveTokenEndpoint(
            settings,
            new TestEnvironment(environment)
        );

        await Assert.That(endpoint.AbsoluteUri).IsEqualTo("https://app.example.test/connect/token");
    }

    [Test]
    [Arguments("http://localhost:8096/connect/token")]
    [Arguments("http://127.0.0.1:8096/connect/token")]
    [Arguments("http://[::1]:8096/connect/token")]
    public async Task Development_AdvertisesLiteralLoopbackWithoutChangingIdentity(string url)
    {
        var settings = new AuthSettings
        {
            Issuer = "http://zeeq-web.localhost:8095/",
            Resource = "http://zeeq-web.localhost:8095/mcp",
            FrontendBaseUri = "http://zeeq-web.localhost:8095",
            DevelopmentTokenEndpoint = url,
        };

        var endpoint = SetupIdentityExtension.ResolveTokenEndpoint(
            settings,
            new TestEnvironment(Environments.Development)
        );

        await Assert.That(endpoint.AbsoluteUri).IsEqualTo(url);
        await Assert.That(settings.Issuer).IsEqualTo("http://zeeq-web.localhost:8095/");
        await Assert.That(settings.Resource).IsEqualTo("http://zeeq-web.localhost:8095/mcp");
        await Assert.That(settings.FrontendBaseUri).IsEqualTo("http://zeeq-web.localhost:8095");
    }

    [Test]
    [Arguments("Production")]
    [Arguments("Staging")]
    public async Task NonDevelopment_RejectsOverride(string environment)
    {
        var settings = new AuthSettings
        {
            DevelopmentTokenEndpoint = "http://localhost:8096/connect/token",
        };

        void Resolve() =>
            SetupIdentityExtension.ResolveTokenEndpoint(settings, new TestEnvironment(environment));

        await Assert.That(Resolve).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("http://zeeq-web.localhost:8095/connect/token")]
    [Arguments("http://127.0.0.2:8096/connect/token")]
    [Arguments("http://example.test/connect/token")]
    [Arguments("https://localhost:8096/connect/token")]
    [Arguments("http://user:password@localhost:8096/connect/token")]
    [Arguments("http://localhost:8096/connect/token?target=other")]
    [Arguments("http://localhost:8096/connect/token#fragment")]
    [Arguments("http://localhost:8096/other")]
    [Arguments("http://localhost:0/connect/token")]
    [Arguments("http://localhost:65536/connect/token")]
    [Arguments("/connect/token")]
    [Arguments("")]
    public async Task Development_RejectsUnsupportedTokenAddress(string url)
    {
        var settings = new AuthSettings { DevelopmentTokenEndpoint = url };

        void Resolve() =>
            SetupIdentityExtension.ResolveTokenEndpoint(
                settings,
                new TestEnvironment(Environments.Development)
            );

        await Assert.That(Resolve).Throws<InvalidOperationException>();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Zeeq.Core.Identity.Tests";
        public string ContentRootPath { get; set; } = Environment.CurrentDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
