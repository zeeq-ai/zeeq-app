using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Zeeq.Core.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Creates short-lived JWTs used to authenticate as the GitHub App.
/// </summary>
/// <remarks>
/// GitHub App JWTs are derived from the configured private key and are used to
/// ask GitHub for installation access tokens. GitHub limits these JWTs to a
/// ten-minute lifetime, so this factory keeps only a short process-local cache.
/// The cache avoids repeated RSA signing during bursts without turning the app
/// JWT into durable state.
/// </remarks>
public sealed class GitHubAppJwtFactory(GitHubSettings settings, IMemoryCache memoryCache)
{
    private const string CacheKey = "zeeq.github.app-jwt";
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan IssuedAtClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Creates a signed app JWT suitable for Octokit GitHub App API calls.
    /// </summary>
    /// <remarks>
    /// The JWT is cached for less than its signed lifetime so callers do not
    /// pay the RSA signing cost on every GitHub App API request. Installation
    /// access tokens are cached separately; this cache only covers the app JWT
    /// used to ask GitHub for those tokens.
    /// </remarks>
    public string CreateJwt() =>
        memoryCache.GetOrCreate(
            CacheKey,
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheLifetime;

                return CreateJwtCore();
            }
        )!;

    private string CreateJwtCore()
    {
        if (string.IsNullOrWhiteSpace(settings.AppId))
        {
            throw new InvalidOperationException("AppSettings:GitHub:AppId is required.");
        }

        if (string.IsNullOrWhiteSpace(settings.PrivateKeyPem))
        {
            throw new InvalidOperationException("AppSettings:GitHub:PrivateKeyPem is required.");
        }

        using var rsa = RSA.Create();

        rsa.ImportFromPem(NormalizePem(settings.PrivateKeyPem));

        var now = DateTimeOffset.UtcNow;

        // CacheSignatureProviders = false prevents the CryptoProviderFactory from caching the
        // AsymmetricSignatureProvider across calls. Without this, the factory caches the provider
        // by KeyId (derived from key material), so a subsequent call with a new RSA instance
        // (same key, same KeyId) retrieves the cached provider that holds the disposed RSA from
        // the previous call, causing ObjectDisposedException.
        var credentials = new SigningCredentials(
            new RsaSecurityKey(rsa),
            SecurityAlgorithms.RsaSha256
        )
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };

        var token = new JwtSecurityToken(
            issuer: settings.AppId,
            audience: null,
            claims:
            [
                new Claim(
                    JwtRegisteredClaimNames.Iat,
                    now.Subtract(IssuedAtClockSkew).ToUnixTimeSeconds().ToString()
                ),
            ],
            notBefore: now.Subtract(IssuedAtClockSkew).UtcDateTime,
            expires: now.Add(JwtLifetime).UtcDateTime,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string NormalizePem(string pem) =>
        pem.Replace("\\n", "\n", StringComparison.Ordinal);
}
