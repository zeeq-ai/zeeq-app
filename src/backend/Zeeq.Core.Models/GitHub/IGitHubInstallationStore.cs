namespace Zeeq.Core.Models;

/// <summary>
/// Store for GitHub App installations linked to Zeeq organizations.
/// </summary>
/// <remarks>
/// Lives in <c>Zeeq.Core.Models</c> rather than a feature package because
/// both <c>Zeeq.Integrations.GitHub</c> (installation callback, token
/// resolution, webhook processing) and <c>Zeeq.Platform.CodeReviews</c>
/// (check runs, review requests) depend on it, and those two packages cannot
/// depend on each other without a circular project reference —
/// <c>Zeeq.Integrations.GitHub</c> already depends on
/// <c>Zeeq.Platform.CodeReviews</c> for <c>ICodeRepositoryStore</c>. This
/// package is the lowest shared layer both already reference.
/// </remarks>
public interface IGitHubInstallationStore
{
    /// <summary>
    /// Finds an active GitHub installation by GitHub installation id.
    /// </summary>
    Task<GitHubAppInstallation?> FindByInstallationIdAsync(
        long installationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds the active GitHub installation linked to an organization.
    /// </summary>
    Task<GitHubAppInstallation?> FindActiveForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Inserts or updates an installation linked to the supplied organization.
    /// </summary>
    Task<GitHubAppInstallation> UpsertLinkedInstallationAsync(
        GitHubAppInstallation installation,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Applies a lifecycle change reported by an <c>installation</c> webhook event.
    /// </summary>
    /// <remarks>
    /// Webhook installation lifecycle events (suspend, unsuspend, deleted, new
    /// permissions accepted) are the only source of truth for these fields
    /// between browser install-callback runs. Without reconciling them here, a
    /// suspended or deleted installation can keep looking active in Zeeq long
    /// after GitHub has silently stopped delivering webhooks for it. No-ops
    /// when the installation id has no linked row yet; the browser callback
    /// owns first-link creation.
    /// </remarks>
    /// <param name="installationId">GitHub installation id from the webhook payload.</param>
    /// <param name="repositorySelection">Current repository selection mode reported by GitHub.</param>
    /// <param name="suspendedAtUtc">Suspension time reported by GitHub, or <see langword="null"/> if active.</param>
    /// <param name="deletedAtUtc">Deletion time, or <see langword="null"/> if still installed.</param>
    /// <param name="cancellationToken">Cancellation token for the store write.</param>
    Task ApplyLifecycleEventAsync(
        long installationId,
        string repositorySelection,
        DateTimeOffset? suspendedAtUtc,
        DateTimeOffset? deletedAtUtc,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Raised when a GitHub installation is already linked to a different Zeeq owner.
/// </summary>
public sealed class GitHubInstallationLinkConflictException(
    long installationId,
    string existingOrganizationId,
    string? existingTeamId,
    string requestedOrganizationId,
    string? requestedTeamId
)
    : InvalidOperationException(
        $"GitHub installation {installationId} is already linked to a different organization/team."
    )
{
    /// <summary>
    /// GitHub installation id that could not be relinked.
    /// </summary>
    public long InstallationId { get; } = installationId;

    /// <summary>
    /// Organization currently linked to the installation.
    /// </summary>
    public string ExistingOrganizationId { get; } = existingOrganizationId;

    /// <summary>
    /// Team currently linked to the installation.
    /// </summary>
    public string? ExistingTeamId { get; } = existingTeamId;

    /// <summary>
    /// Organization requested by the current callback state token.
    /// </summary>
    public string RequestedOrganizationId { get; } = requestedOrganizationId;

    /// <summary>
    /// Team requested by the current callback state token.
    /// </summary>
    public string? RequestedTeamId { get; } = requestedTeamId;
}
