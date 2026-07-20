using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Documents;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Lists repository mappings already configured for the current Zeeq organization.
/// </summary>
public sealed class ListConfiguredGitHubRepositoriesHandler(ICodeRepositoryStore repositories)
    : IEndpointHandler
{
    /// <summary>
    /// Returns active repository mappings for the organization in the authenticated session.
    /// </summary>
    public async Task<IResult> HandleAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        var configured = await repositories.ListConfiguredForOrganizationAsync(
            organizationId,
            cancellationToken
        );

        return Results.Ok(configured.Select(GitHubRepositoryEndpointSupport.ToResponse).ToArray());
    }
}

/// <summary>
/// Lists repositories currently accessible to the linked GitHub App installation.
/// </summary>
/// <remarks>
/// The response includes configured-state metadata so the settings UI can show
/// one list that distinguishes "available from GitHub" from "already configured
/// in Zeeq", including mappings that are currently paused.
/// </remarks>
public sealed class ListAvailableGitHubRepositoriesHandler(
    ICodeRepositoryStore repositories,
    IGitHubRepositoryProvider provider
) : IEndpointHandler
{
    /// <summary>
    /// Returns installation-visible repositories plus current Zeeq mapping status.
    /// </summary>
    public async Task<IResult> HandleAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        IReadOnlyList<GitHubAvailableRepository> available;
        try
        {
            available = await provider.ListAvailableAsync(organizationId, cancellationToken);
        }
        catch (GitHubInstallationUnavailableException)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError(
                    "No active GitHub App installation is linked to this organization."
                )
            );
        }

        var configuredByName = (
            await repositories.ListConfiguredForOrganizationAsync(organizationId, cancellationToken)
        ).ToDictionary(
            repository => repository.OwnerQualifiedName,
            StringComparer.OrdinalIgnoreCase
        );

        return Results.Ok<IReadOnlyList<GitHubAvailableRepositoryResponse>>([
            .. available.Select(repository =>
            {
                configuredByName.TryGetValue(repository.OwnerQualifiedName, out var configured);

                return new GitHubAvailableRepositoryResponse(
                    repository.GitHubRepositoryId,
                    repository.NodeId,
                    repository.Name,
                    repository.OwnerQualifiedName,
                    repository.Private,
                    repository.DefaultBranch,
                    repository.HtmlUrl,
                    configured is not null,
                    configured?.Id,
                    configured?.VisibleInLibraryPicker ?? true
                );
            }),
        ]);
    }
}

/// <summary>
/// Registers a GitHub repository mapping for the current organization.
/// </summary>
/// <remarks>
/// This is the key repository-registration path for webhook ingress. The handler
/// validates the requested owner/name against the GitHub App installation's live
/// repository list before writing a local mapping. That prevents an operator or
/// stale UI from creating rows that GitHub cannot actually deliver.
/// </remarks>
public sealed class CreateGitHubRepositoryMappingHandler(
    ICodeRepositoryStore repositories,
    IGitHubRepositoryProvider provider,
    ILibraryDocumentStore libraries
) : IEndpointHandler
{
    /// <summary>
    /// Creates or refreshes the active Zeeq mapping for a GitHub repository.
    /// </summary>
    public async Task<IResult> HandleAsync(
        GitHubCreateRepositoryMappingRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        if (
            !GitHubRepositoryEndpointSupport.TryNormalizeOwnerQualifiedName(
                request.OwnerQualifiedName,
                out var requestedName
            )
        )
        {
            return Results.BadRequest(
                new GitHubRepositoryManagementError(
                    "OwnerQualifiedName must use the GitHub owner/repository format."
                )
            );
        }

        IReadOnlyList<GitHubAvailableRepository> available;
        try
        {
            available = await provider.ListAvailableAsync(organizationId, cancellationToken);
        }
        catch (GitHubInstallationUnavailableException)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError(
                    "No active GitHub App installation is linked to this organization."
                )
            );
        }

        var gitHubRepository = available.FirstOrDefault(repository =>
            string.Equals(
                repository.OwnerQualifiedName,
                requestedName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (gitHubRepository is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError(
                    "The linked GitHub App installation cannot access that repository."
                )
            );
        }

        var libraryIds = request.LibraryIds ?? [];
        if (libraryIds.Length > 0)
        {
            var validationError = await GitHubRepositoryEndpointSupport.ValidateLibraryIdsAsync(
                libraryIds,
                organizationId,
                libraries,
                cancellationToken
            );
            if (validationError is not null)
            {
                return Results.BadRequest(new GitHubRepositoryManagementError(validationError));
            }
        }

        var now = DateTimeOffset.UtcNow;
        var repository = new CodeRepository
        {
            Id = "repo_" + Guid.CreateVersion7().ToString("N"),
            OrganizationId = organizationId,
            TeamId = GitHubRepositoryEndpointSupport.NormalizeOptional(request.TeamId),
            Provider = GitHubRepositoryEndpointSupport.Provider,
            OwnerQualifiedName = gitHubRepository.OwnerQualifiedName,
            DisplayName = GitHubRepositoryEndpointSupport.DisplayNameOrDefault(
                request.DisplayName,
                gitHubRepository.OwnerQualifiedName
            ),
            Enabled = request.Enabled,
            VisibleInLibraryPicker = request.VisibleInLibraryPicker,
            LibraryIds = libraryIds,
            ReviewConfiguration = GitHubRepositoryEndpointSupport.ToReviewConfiguration(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var saved = await repositories.UpsertAsync(repository, cancellationToken);

        return Results.Created(
            $"/api/v1/orgs/{Uri.EscapeDataString(organizationId)}/integrations/github/repositories/{Uri.EscapeDataString(saved.Id)}",
            GitHubRepositoryEndpointSupport.ToResponse(saved)
        );
    }
}

/// <summary>
/// Updates local settings for a configured GitHub repository mapping.
/// </summary>
public sealed class UpdateGitHubRepositoryMappingHandler(
    ICodeRepositoryStore repositories,
    ILibraryDocumentStore libraries
) : IEndpointHandler
{
    /// <summary>
    /// Updates display/team/enabled settings without changing the provider repository identity.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string repositoryId,
        GitHubUpdateRepositoryMappingRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        var existing = await repositories.FindActiveForOrganizationAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );

        if (existing is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError("Repository mapping was not found.")
            );
        }

        var incomingLibraryIds = request.LibraryIds;
        if (incomingLibraryIds is { Length: > 0 })
        {
            var validationError = await GitHubRepositoryEndpointSupport.ValidateLibraryIdsAsync(
                incomingLibraryIds,
                organizationId,
                libraries,
                cancellationToken
            );
            if (validationError is not null)
            {
                return Results.BadRequest(new GitHubRepositoryManagementError(validationError));
            }
        }

        // null = keep existing; [] = clear; non-empty = replace
        var effectiveLibraryIds = incomingLibraryIds ?? existing.LibraryIds ?? [];

        existing.TeamId = GitHubRepositoryEndpointSupport.NormalizeOptional(request.TeamId);
        existing.DisplayName = GitHubRepositoryEndpointSupport.DisplayNameOrDefault(
            request.DisplayName,
            existing.DisplayName
        );
        existing.Enabled = request.Enabled;
        existing.VisibleInLibraryPicker =
            request.VisibleInLibraryPicker ?? existing.VisibleInLibraryPicker;
        existing.LibraryIds = effectiveLibraryIds;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var saved = await repositories.UpsertAsync(existing, cancellationToken);

        return Results.Ok(GitHubRepositoryEndpointSupport.ToResponse(saved));
    }
}

/// <summary>
/// Updates whether a GitHub repository appears as a private library source.
/// </summary>
/// <remarks>
/// Visibility is independent from webhook/code-review enablement. This handler
/// can create a local repository row with <c>Enabled = false</c> so a repository
/// can be hidden before it is enabled for webhook-triggered review work.
/// </remarks>
public sealed class UpdateGitHubRepositoryVisibilityHandler(
    ICodeRepositoryStore repositories,
    IGitHubRepositoryProvider provider
) : IEndpointHandler
{
    /// <summary>
    /// Updates picker visibility for an installation-visible GitHub repository.
    /// </summary>
    public async Task<IResult> HandleAsync(
        GitHubUpdateRepositoryVisibilityRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        if (
            !GitHubRepositoryEndpointSupport.TryNormalizeOwnerQualifiedName(
                request.OwnerQualifiedName,
                out var requestedName
            )
        )
        {
            return Results.BadRequest(
                new GitHubRepositoryManagementError(
                    "OwnerQualifiedName must use the GitHub owner/repository format."
                )
            );
        }

        IReadOnlyList<GitHubAvailableRepository> available;
        try
        {
            available = await provider.ListAvailableAsync(organizationId, cancellationToken);
        }
        catch (GitHubInstallationUnavailableException)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError(
                    "No active GitHub App installation is linked to this organization."
                )
            );
        }

        var gitHubRepository = available.FirstOrDefault(repository =>
            string.Equals(
                repository.OwnerQualifiedName,
                requestedName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        if (gitHubRepository is null)
        {
            return Results.NotFound(
                new GitHubRepositoryManagementError(
                    "The linked GitHub App installation cannot access that repository."
                )
            );
        }

        var existing = (
            await repositories.ListConfiguredForOrganizationAsync(organizationId, cancellationToken)
        ).FirstOrDefault(repository =>
            string.Equals(
                repository.OwnerQualifiedName,
                gitHubRepository.OwnerQualifiedName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            existing.VisibleInLibraryPicker = request.VisibleInLibraryPicker;
            existing.UpdatedAtUtc = now;

            var updated = await repositories.UpsertAsync(existing, cancellationToken);

            return Results.Ok(GitHubRepositoryEndpointSupport.ToResponse(updated));
        }

        // NOTE: Library-picker visibility is independent from webhook enablement.
        // A repository can be hidden or shown as a library source before it is
        // enabled for webhook-triggered code-review work.
        var repository = new CodeRepository
        {
            Id = "repo_" + Guid.CreateVersion7().ToString("N"),
            OrganizationId = organizationId,
            TeamId = null,
            Provider = GitHubRepositoryEndpointSupport.Provider,
            OwnerQualifiedName = gitHubRepository.OwnerQualifiedName,
            DisplayName = gitHubRepository.OwnerQualifiedName,
            Enabled = false,
            VisibleInLibraryPicker = request.VisibleInLibraryPicker,
            LibraryIds = [],
            ReviewConfiguration = GitHubRepositoryEndpointSupport.ToReviewConfiguration(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var created = await repositories.UpsertAsync(repository, cancellationToken);

        return Results.Ok(GitHubRepositoryEndpointSupport.ToResponse(created));
    }
}

/// <summary>
/// Disables a configured GitHub repository mapping for the current organization.
/// </summary>
/// <remarks>
/// This endpoint removes the current registration row from management and
/// webhook gating while preserving it for history. A reversible pause should use
/// the update endpoint with <c>Enabled = false</c>; reconnecting after DELETE
/// creates a fresh active mapping for the same GitHub repository.
/// </remarks>
public sealed class DisableGitHubRepositoryMappingHandler(ICodeRepositoryStore repositories)
    : IEndpointHandler
{
    /// <summary>
    /// Soft-disables a mapping so future webhooks for the repository are filtered out.
    /// </summary>
    public async Task<IResult> HandleAsync(
        string repositoryId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Unauthorized();
        }

        var disabled = await repositories.DisableAsync(
            organizationId,
            repositoryId,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        return disabled
            ? Results.NoContent()
            : Results.NotFound(
                new GitHubRepositoryManagementError("Repository mapping was not found.")
            );
    }
}

file static class GitHubRepositoryEndpointSupport
{
    public const string Provider = "github";

    public static bool TryNormalizeOwnerQualifiedName(string? value, out string ownerQualifiedName)
    {
        ownerQualifiedName = (value ?? string.Empty).Trim();
        var parts = ownerQualifiedName.Split('/', StringSplitOptions.TrimEntries);

        return parts is [var owner, var repository]
            && !string.IsNullOrWhiteSpace(owner)
            && !string.IsNullOrWhiteSpace(repository);
    }

    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string DisplayNameOrDefault(string? displayName, string fallback) =>
        string.IsNullOrWhiteSpace(displayName) ? fallback : displayName.Trim();

    public static GitHubConfiguredRepositoryResponse ToResponse(CodeRepository repository) =>
        new(
            repository.Id,
            repository.TeamId,
            repository.OwnerQualifiedName,
            repository.DisplayName,
            repository.Enabled,
            repository.VisibleInLibraryPicker,
            repository.LibraryIds,
            repository.CreatedAtUtc,
            repository.UpdatedAtUtc
        );

    /// <summary>
    /// Validates that all <paramref name="libraryIds"/> exist and belong to
    /// <paramref name="organizationId"/>. Returns a human-readable error message
    /// when any ID is unknown; returns <c>null</c> when all IDs are valid.
    /// </summary>
    public static async Task<string?> ValidateLibraryIdsAsync(
        string[] libraryIds,
        string organizationId,
        ILibraryDocumentStore libraries,
        CancellationToken cancellationToken
    )
    {
        var orgLibraries = await libraries.ListLibrariesAsync(organizationId, cancellationToken);
        var validIds = orgLibraries.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var unknownIds = libraryIds.Where(id => !validIds.Contains(id)).ToArray();

        return unknownIds.Length > 0
            ? $"Unknown or cross-organization library IDs: {string.Join(", ", unknownIds)}."
            : null;
    }

    // NOTE: This intentionally replaces the old provider metadata payload in
    // configuration_json. Agent code reviews have not been deployed to production
    // yet, and local development data is empty, so there is no legacy repository
    // configuration data to preserve for this migration.
    public static CodeRepositoryReviewConfiguration ToReviewConfiguration() =>
        CodeRepositoryReviewConfiguration.Empty;
}
