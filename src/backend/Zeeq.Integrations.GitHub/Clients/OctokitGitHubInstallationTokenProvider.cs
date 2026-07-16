using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Octokit-backed implementation of <see cref="IGitHubInstallationTokenProvider"/>.
/// </summary>
/// <remarks>
/// Shares the same cache key format and TTL as
/// <see cref="OctokitGitHubClientFactory"/> so a repository ingest run and an
/// ordinary API call for the same installation reuse one cached token instead
/// of minting twice.
/// </remarks>
internal sealed partial class OctokitGitHubInstallationTokenProvider(
    IGitHubInstallationTokenClient tokenClient,
    HybridCache cache,
    ILogger<OctokitGitHubInstallationTokenProvider> logger
) : IGitHubInstallationTokenProvider
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = OctokitGitHubClientFactory.InstallationTokenCacheTtl,
        LocalCacheExpiration = OctokitGitHubClientFactory.InstallationTokenLocalCacheTtl,
    };

    /// <inheritdoc />
    public async Task<string> GetInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken
    )
    {
        var cacheKey = $"github:installation-token:{installationId}";

        return await cache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                LogTokenCacheMiss(logger, installationId);

                return await tokenClient.CreateInstallationTokenAsync(installationId, ct);
            },
            CacheOptions,
            tags: ["github", $"github:installation:{installationId}"],
            cancellationToken: cancellationToken
        );
    }

    [LoggerMessage(
        EventId = 3134,
        Level = LogLevel.Debug,
        Message = "GitHub installation token cache miss. InstallationId={InstallationId}"
    )]
    private static partial void LogTokenCacheMiss(ILogger logger, long installationId);
}
