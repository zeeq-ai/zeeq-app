using System.ComponentModel.DataAnnotations;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// GitHub repository visible to the installed Zeeq GitHub App.
/// </summary>
/// <remarks>
/// This is the provider-facing shape returned by <see cref="IGitHubRepositoryProvider"/>.
/// It deliberately contains only repository metadata needed to register a
/// Zeeq mapping and render the management UI. Code-review workflow data stays
/// on provider-neutral repository and pull request models.
/// </remarks>
public sealed record GitHubAvailableRepository(
    long GitHubRepositoryId,
    string NodeId,
    string Name,
    string OwnerQualifiedName,
    bool Private,
    string DefaultBranch,
    string HtmlUrl
);

/// <summary>
/// Response for a repository already configured in Zeeq.
/// </summary>
public sealed record GitHubConfiguredRepositoryResponse(
    string Id,
    string? TeamId,
    string OwnerQualifiedName,
    string DisplayName,
    bool Enabled,
    string[] LibraryIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>
/// Response for a GitHub repository available to the current installation.
/// </summary>
public sealed record GitHubAvailableRepositoryResponse(
    long GitHubRepositoryId,
    string NodeId,
    string Name,
    string OwnerQualifiedName,
    bool Private,
    string DefaultBranch,
    string HtmlUrl,
    bool Configured,
    string? ConfiguredRepositoryId
);

/// <summary>
/// Request to register a GitHub repository for Zeeq code review.
/// </summary>
/// <remarks>
/// The organization comes from the authenticated Zeeq session, not from this
/// body. The handler verifies that <see cref="OwnerQualifiedName"/> is currently
/// visible to the linked GitHub App installation before creating the mapping.
/// </remarks>
public sealed record GitHubCreateRepositoryMappingRequest(
    [property: Required, MaxLength(512)] string OwnerQualifiedName,
    [property: MaxLength(128)] string? TeamId,
    [property: MaxLength(256)] string? DisplayName,
    bool Enabled = true,
    string[]? LibraryIds = null
);

/// <summary>
/// Request to update local management fields on an existing repository mapping.
/// </summary>
/// <remarks>
/// Provider identity is intentionally immutable here. To point Zeeq at a
/// different GitHub repository, disable this mapping and create a new one from
/// the installation's available repository list.
/// </remarks>
/// <remarks>
/// <see cref="LibraryIds"/> follows a three-way null/empty/populated convention:
/// <c>null</c> leaves the existing mapping unchanged; <c>[]</c> clears all mappings;
/// a non-empty array replaces the mapping with exactly those library IDs.
/// </remarks>
public sealed record GitHubUpdateRepositoryMappingRequest(
    [property: MaxLength(128)] string? TeamId,
    [property: MaxLength(256)] string? DisplayName,
    bool Enabled = true,
    string[]? LibraryIds = null
);

/// <summary>
/// Error response returned by GitHub repository management endpoints.
/// </summary>
public sealed record GitHubRepositoryManagementError(string Message);
