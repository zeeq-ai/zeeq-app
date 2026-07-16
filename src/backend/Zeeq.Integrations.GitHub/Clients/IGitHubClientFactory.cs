using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Creates GitHub clients authenticated as the Zeeq GitHub App installation.
/// </summary>
/// <remarks>
/// Queue handlers and GitHub mutation services should depend on this interface
/// instead of creating Octokit clients directly. The factory resolves the active
/// installation from Zeeq's installation store, mints or reuses a cached
/// installation token, and returns a client ready for repository, pull request,
/// comment, review, and reaction API calls.
///
/// The factory is GitHub-specific because it returns Octokit's
/// <see cref="GitHubClient"/>. Provider-neutral code-review services should stay
/// behind their own contracts and call this only from GitHub adapter classes.
/// </remarks>
public interface IGitHubClientFactory
{
    /// <summary>
    /// Creates an installation-authenticated GitHub client for the organization.
    /// </summary>
    /// <param name="organizationId">
    /// Zeeq organization id whose active GitHub App installation should be used.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the local store and cache lookup. Octokit 14 does not expose a
    /// cancellation-token overload for creating installation tokens, so an
    /// already in-flight token request cannot be cancelled by this parameter.
    /// </param>
    /// <returns>
    /// An Octokit client using a GitHub App installation token.
    /// </returns>
    Task<GitHubClient> CreateInstallationClientForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Raised when Zeeq cannot find an active GitHub App installation for an organization.
/// </summary>
/// <remarks>
/// This is treated as a configuration failure. Webhook ingress should already
/// resolve configured repositories before queue work is published, so handlers
/// that reach this exception likely need the operator to reconnect the GitHub
/// App or repair installation lifecycle state.
/// </remarks>
public sealed class GitHubInstallationUnavailableException(string organizationId)
    : InvalidOperationException(
        $"No active GitHub App installation is linked to organization {organizationId}."
    )
{
    /// <summary>
    /// Organization id that did not have an active GitHub App installation.
    /// </summary>
    public string OrganizationId { get; } = organizationId;
}
