using Microsoft.Extensions.Logging;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Creates GitHub App installation access tokens through Octokit.
/// </summary>
/// <remarks>
/// This is the only class in the client-factory slice that calls GitHub to mint
/// installation tokens. Keeping that side effect behind a narrow interface makes
/// factory tests small and avoids mocking the broad Octokit client graph.
///
/// GitHub installation tokens are short-lived bearer tokens. Log messages in
/// this class include only installation ids and status details; they must never
/// include the token value or the app private key.
/// </remarks>
internal interface IGitHubInstallationTokenClient
{
    /// <summary>
    /// Creates a new installation access token for a GitHub App installation.
    /// </summary>
    /// <param name="installationId">GitHub App installation id verified and stored by Zeeq.</param>
    /// <param name="cancellationToken">
    /// Cancellation token checked before the Octokit call. Octokit 14 does not
    /// expose a cancellation-token overload for this specific endpoint.
    /// </param>
    /// <returns>The GitHub installation access token string.</returns>
    Task<string> CreateInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Octokit implementation of <see cref="IGitHubInstallationTokenClient"/>.
/// </summary>
internal sealed partial class OctokitGitHubInstallationTokenClient(
    GitHubAppJwtFactory jwtFactory,
    GitHubConnectionFactory connectionFactory,
    ILogger<OctokitGitHubInstallationTokenClient> logger
) : IGitHubInstallationTokenClient
{
    /// <inheritdoc />
    public async Task<string> CreateInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var appClient = connectionFactory.CreateClient(
            new Credentials(jwtFactory.CreateJwt(), AuthenticationType.Bearer)
        );

        try
        {
            var token = await appClient.GitHubApps.CreateInstallationToken(installationId);

            return token.Token;
        }
        catch (RateLimitExceededException ex)
        {
            LogRateLimitFailure(
                logger,
                installationId,
                ex.Limit,
                ex.Remaining,
                ex.Reset.ToString("O")
            );

            throw;
        }
        catch (AuthorizationException ex)
        {
            LogPermissionFailure(logger, installationId, ex.StatusCode.ToString());

            throw;
        }
        catch (ForbiddenException ex)
        {
            LogPermissionFailure(logger, installationId, ex.StatusCode.ToString());

            throw;
        }
    }

    [LoggerMessage(
        EventId = 3132,
        Level = LogLevel.Warning,
        Message = "GitHub rate limit while creating installation token. InstallationId={InstallationId}, Limit={Limit}, Remaining={Remaining}, Reset={Reset}"
    )]
    private static partial void LogRateLimitFailure(
        ILogger logger,
        long installationId,
        int limit,
        int remaining,
        string reset
    );

    [LoggerMessage(
        EventId = 3133,
        Level = LogLevel.Warning,
        Message = "GitHub permission failure while creating installation token. InstallationId={InstallationId}, Status={Status}"
    )]
    private static partial void LogPermissionFailure(
        ILogger logger,
        long installationId,
        string status
    );
}
