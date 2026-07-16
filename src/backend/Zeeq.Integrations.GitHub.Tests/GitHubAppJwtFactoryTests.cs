using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Zeeq.Core.Common;
using Zeeq.Integrations.GitHub;
using Microsoft.Extensions.Caching.Memory;

namespace Zeeq.Integrations.GitHub.Tests;

public sealed class GitHubAppJwtFactoryTests
{
    [Test]
    public async Task CreateJwt_WithEscapedPemNewlines_ReturnsAppJwt()
    {
        using var rsa = RSA.Create(2048);
        var escapedPem = rsa.ExportRSAPrivateKeyPem()
            .Replace("\n", "\\n", StringComparison.Ordinal);
        var factory = new GitHubAppJwtFactory(
            new GitHubSettings { AppId = "12345", PrivateKeyPem = escapedPem },
            CreateMemoryCache()
        );

        var token = factory.CreateJwt();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        await Assert.That(jwt.Issuer).IsEqualTo("12345");
        await Assert.That(jwt.ValidTo).IsGreaterThan(DateTime.UtcNow);
    }

    [Test]
    public async Task CreateJwt_WhenCalledRepeatedly_ReturnsCachedJwt()
    {
        using var rsa = RSA.Create(2048);
        var factory = new GitHubAppJwtFactory(
            new GitHubSettings { AppId = "12345", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
            CreateMemoryCache()
        );

        var first = factory.CreateJwt();
        var second = factory.CreateJwt();

        await Assert.That(second).IsEqualTo(first);
    }

    private static MemoryCache CreateMemoryCache() => new(new MemoryCacheOptions());
}
