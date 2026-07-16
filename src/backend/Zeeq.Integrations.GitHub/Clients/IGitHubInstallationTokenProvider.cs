namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Resolves raw GitHub App installation access tokens for callers that need the
/// token string itself rather than an Octokit <c>GitHubClient</c> — for example
/// injecting a <c>git</c>/<c>gh</c> CLI credential.
/// </summary>
public interface IGitHubInstallationTokenProvider
{
    /// <summary>
    /// Returns a cached or freshly-minted installation access token for the
    /// given installation id. Never log the returned value.
    /// </summary>
    Task<string> GetInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken
    );
}
