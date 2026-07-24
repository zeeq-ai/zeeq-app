using System.Text.Json;
using Zeeq.Core.Common;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Verifies a GitHub App installation id and returns normalized installation details.
/// </summary>
/// <remarks>
/// This interface is the GitHub API boundary for the install callback. The
/// callback receives an <c>installation_id</c> query parameter from GitHub, but
/// Zeeq should not trust that id until GitHub confirms it belongs to the
/// configured GitHub App. Implementations authenticate as the app, fetch the
/// installation, and return only the fields Zeeq needs to persist the local
/// <see cref="Zeeq.Core.Models.GitHubAppInstallation"/> row.
/// </remarks>
public interface IGitHubInstallationVerifier
{
    /// <summary>
    /// Verifies the installation using GitHub App authentication.
    /// </summary>
    /// <param name="installationId">GitHub installation id received from the setup callback.</param>
    /// <param name="cancellationToken">Cancellation token for the GitHub API call.</param>
    /// <returns>Normalized installation details safe for the callback to persist.</returns>
    Task<GitHubInstallationVerification> VerifyAsync(
        long installationId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Normalized GitHub App installation details.
/// </summary>
/// <remarks>
/// Octokit exposes GitHub's full installation shape. This record keeps the
/// callback and store isolated from that external type so persistence does not
/// depend on Octokit models. The raw JSON is kept for diagnostics and future
/// reconciliation, while first-class properties cover routing and display needs.
/// </remarks>
/// <param name="InstallationId">GitHub installation id verified by the app-authenticated API call.</param>
/// <param name="AccountLogin">Login for the GitHub user or organization where the app is installed.</param>
/// <param name="AccountId">Immutable GitHub account id for the installed target.</param>
/// <param name="AccountType">Installed account type, normally <c>User</c> or <c>Organization</c>.</param>
/// <param name="RepositorySelection">Repository selection mode, normally <c>all</c> or <c>selected</c>.</param>
/// <param name="SuspendedAtUtc">Suspension time reported by GitHub, if the installation is suspended.</param>
/// <param name="RawInstallationJson">Serialized GitHub installation payload for diagnostics.</param>
public sealed record GitHubInstallationVerification(
    long InstallationId,
    string AccountLogin,
    long AccountId,
    string AccountType,
    string RepositorySelection,
    DateTimeOffset? SuspendedAtUtc,
    string RawInstallationJson
);

/// <summary>
/// Octokit-backed verifier for GitHub App installations.
/// </summary>
/// <remarks>
/// This class only verifies and normalizes GitHub state. It does not decide
/// which Zeeq organization owns the installation and does not write to the
/// database; <see cref="GitHubInstallationCallbackHandler"/> and
/// <see cref="Zeeq.Core.Models.IGitHubInstallationStore"/> own those
/// parts of the lifecycle.
/// </remarks>
internal sealed class OctokitGitHubInstallationVerifier(
    GitHubSettings settings,
    GitHubAppJwtFactory jwtFactory,
    GitHubConnectionFactory connectionFactory
) : IGitHubInstallationVerifier
{
    public async Task<GitHubInstallationVerification> VerifyAsync(
        long installationId,
        CancellationToken cancellationToken
    )
    {
        // GitHub App installation metadata must be fetched with an app JWT, not
        // a user token or installation token. The callback has no user-level
        // GitHub token in this phase.
        var client = connectionFactory.CreateClient(
            new Credentials(jwtFactory.CreateJwt(), AuthenticationType.Bearer)
        );

        var installation = await client.GitHubApps.GetInstallationForCurrent(installationId);

        // Fail closed if GitHub says the installation belongs to a different
        // app. This protects local state from linking an installation id that was
        // not issued for the configured Zeeq GitHub App.
        if (
            long.TryParse(settings.AppId, out var configuredAppId)
            && installation.AppId != configuredAppId
        )
        {
            throw new InvalidOperationException(
                $"GitHub installation {installationId} belongs to app {installation.AppId}, not configured app {configuredAppId}."
            );
        }

        return new GitHubInstallationVerification(
            InstallationId: installationId,
            AccountLogin: installation.Account.Login,
            AccountId: installation.Account.Id,
            AccountType: installation.TargetType.ToString(),
            RepositorySelection: installation.RepositorySelection.ToString(),
            SuspendedAtUtc: installation.SuspendedAt,
            RawInstallationJson: JsonSerializer.Serialize(installation)
        );
    }
}
