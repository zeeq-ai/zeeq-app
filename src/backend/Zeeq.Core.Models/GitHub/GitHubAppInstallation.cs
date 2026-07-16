namespace Zeeq.Core.Models;

/// <summary>
/// GitHub App installation linked to a Zeeq organization.
/// </summary>
/// <remarks>
/// Backed by <c>code_review_github_app_installations</c>. This is the canonical
/// source for resolving the GitHub installation id for an organization; repository
/// mappings intentionally do not duplicate the installation id.
/// </remarks>
public sealed class GitHubAppInstallation : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Optional Zeeq team context selected when the installation was started.
    /// </summary>
    /// <remarks>
    /// GitHub App installations are organization-level links in this slice, so
    /// this value can be null and the entity intentionally does not implement
    /// <see cref="ITeamScopedEntity"/>.
    /// </remarks>
    public string? TeamId { get; init; }

    /// <summary>
    /// GitHub installation id returned by the GitHub App API.
    /// </summary>
    public long InstallationId { get; init; }

    /// <summary>
    /// Login for the installed GitHub organization or user account.
    /// </summary>
    public required string AccountLogin { get; set; }

    /// <summary>
    /// GitHub account id for the installed organization or user account.
    /// </summary>
    public long AccountId { get; set; }

    /// <summary>
    /// GitHub account type, normally <c>Organization</c> or <c>User</c>.
    /// </summary>
    public required string AccountType { get; set; }

    /// <summary>
    /// Repository selection mode returned by GitHub, normally <c>all</c> or <c>selected</c>.
    /// </summary>
    public required string RepositorySelection { get; set; }

    /// <summary>
    /// When this Zeeq organization first linked the installation.
    /// </summary>
    public DateTimeOffset InstalledAtUtc { get; init; }

    /// <summary>
    /// Set when GitHub reports the installation was suspended.
    /// </summary>
    public DateTimeOffset? SuspendedAtUtc { get; set; }

    /// <summary>
    /// Set when GitHub reports the installation was deleted.
    /// </summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }

    /// <summary>
    /// Raw installation JSON captured from GitHub for diagnostics and future reconciliation.
    /// </summary>
    public string RawInstallationJson { get; set; } = "{}";
}
