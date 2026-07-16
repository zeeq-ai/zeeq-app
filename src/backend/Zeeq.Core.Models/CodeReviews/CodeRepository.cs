namespace Zeeq.Core.Models;

/// <summary>
/// Repository configured for Zeeq code review workflows.
/// </summary>
/// <remarks>
/// This maps an external repository such as <c>owner/repo</c> to a Zeeq
/// organization and optional team. GitHub installation ids intentionally live
/// on <see cref="GitHubAppInstallation"/> so installation lifecycle changes do
/// not create stale repository rows.
/// </remarks>
public sealed class CodeRepository : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Optional team that owns this repository mapping.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// External provider key, for example <c>github</c>.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Provider-qualified repository name, for example <c>owner/repo</c>.
    /// </summary>
    public required string OwnerQualifiedName { get; set; }

    /// <summary>
    /// Human-readable repository name shown in Zeeq.
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// True when webhook events should produce Zeeq work for this repository.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Library ids from this organization that reviewer agents may query when
    /// reviewing pull requests on this repository. Empty means no libraries are
    /// scoped to this repository (reviewers get no library-tool context).
    /// </summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>
    /// Typed review settings stored in the repository configuration JSONB document.
    /// </summary>
    public CodeRepositoryReviewConfiguration ReviewConfiguration { get; set; } =
        CodeRepositoryReviewConfiguration.Empty;
}
