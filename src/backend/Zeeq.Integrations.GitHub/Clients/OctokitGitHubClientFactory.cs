using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Octokit-backed implementation of <see cref="IGitHubClientFactory"/>.
/// </summary>
/// <remarks>
/// The factory deliberately reads installation ids only from
/// <see cref="IGitHubInstallationStore"/>. Repository mappings do not carry a
/// GitHub installation id because installation replacement, suspension, and
/// deletion are lifecycle events owned by the installation table.
///
/// Installation tokens are cached by GitHub installation id for less than one
/// hour because GitHub returns one-hour installation tokens. The cache avoids a
/// GitHub API call for every comment/review mutation while still refreshing
/// before GitHub expires the token.
/// </remarks>
internal sealed partial class OctokitGitHubClientFactory(
    IGitHubInstallationStore installationStore,
    IGitHubInstallationTokenClient tokenClient,
    HybridCache cache,
    ILogger<OctokitGitHubClientFactory> logger
) : IGitHubClientFactory
{
    private static readonly ProductHeaderValue ProductHeader = new("zeeq");
    internal static readonly TimeSpan InstallationTokenCacheTtl = TimeSpan.FromMinutes(55);
    internal static readonly TimeSpan InstallationTokenLocalCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly HybridCacheEntryOptions InstallationTokenCacheOptions = new()
    {
        Expiration = InstallationTokenCacheTtl,
        LocalCacheExpiration = InstallationTokenLocalCacheTtl,
    };

    /// <inheritdoc />
    public async Task<GitHubClient> CreateInstallationClientForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var installation = await installationStore.FindActiveForOrganizationAsync(
            organizationId,
            cancellationToken
        );

        if (installation is null)
        {
            LogMissingInstallation(logger, organizationId);
            throw new GitHubInstallationUnavailableException(organizationId);
        }

        var token = await GetInstallationTokenAsync(installation.InstallationId, cancellationToken);

        return CreateClient(token);
    }

    private async ValueTask<string> GetInstallationTokenAsync(
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
            InstallationTokenCacheOptions,
            tags: ["github", $"github:installation:{installationId}"],
            cancellationToken: cancellationToken
        );
    }

    private static GitHubClient CreateClient(string token) =>
        new(ProductHeader) { Credentials = new Credentials(token) };

    [LoggerMessage(
        EventId = 3130,
        Level = LogLevel.Warning,
        Message = "No active GitHub App installation is linked to organization {OrganizationId}."
    )]
    private static partial void LogMissingInstallation(ILogger logger, string organizationId);

    [LoggerMessage(
        EventId = 3131,
        Level = LogLevel.Debug,
        Message = "GitHub installation token cache miss. InstallationId={InstallationId}"
    )]
    private static partial void LogTokenCacheMiss(ILogger logger, long installationId);
}
