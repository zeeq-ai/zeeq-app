namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for provider-neutral repository mappings used by code review.
/// </summary>
/// <remarks>
/// Repository mappings are the first gate for GitHub webhook work. Webhook
/// ingress resolves a provider repository identity here before publishing any
/// tenant queue messages. This interface is provider-neutral so GitHub-specific
/// code can depend on the mapping concept without tying the platform contract
/// to Postgres.
/// </remarks>
public interface ICodeRepositoryStore
{
    /// <summary>
    /// Finds an enabled repository mapping by provider identity.
    /// </summary>
    /// <remarks>
    /// Webhook payloads start with provider and owner/name, not with a Zeeq
    /// organization. A missing result means Zeeq should acknowledge the event
    /// and do no code-review work for that repository.
    /// </remarks>
    Task<Zeeq.Core.Models.CodeRepository?> FindActiveAsync(
        string provider,
        string ownerQualifiedName,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds an enabled repository mapping by provider identity inside an organization.
    /// </summary>
    /// <remarks>
    /// User-facing tools and management paths already know the caller's
    /// organization and should scope provider/name lookups with it. This avoids
    /// cross-organization ambiguity when the same provider repository is
    /// configured in more than one organization.
    /// </remarks>
    Task<Zeeq.Core.Models.CodeRepository?> FindActiveForOrganizationByProviderIdentityAsync(
        string organizationId,
        string provider,
        string ownerQualifiedName,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists enabled repository mappings for an organization.
    /// </summary>
    /// <remarks>
    /// Used by workflow/cache paths that need the actionable webhook set.
    /// Paused and soft-disabled rows should not appear in this result.
    /// </remarks>
    Task<IReadOnlyList<Zeeq.Core.Models.CodeRepository>> ListActiveForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists non-deleted repository mappings for an organization.
    /// </summary>
    /// <remarks>
    /// Management screens need to show paused mappings where
    /// <see cref="Zeeq.Core.Models.CodeRepository.Enabled"/> is false so an operator can see and
    /// re-enable them. Webhook ingress should not use this method; use
    /// <see cref="FindActiveAsync"/> for the strict enabled gate.
    /// </remarks>
    Task<IReadOnlyList<Zeeq.Core.Models.CodeRepository>> ListConfiguredForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Finds a non-deleted repository mapping by local id inside an organization.
    /// </summary>
    /// <remarks>
    /// Repository management endpoints use the local id because UI actions
    /// mutate one configured mapping. The organization scope is part of the
    /// lookup so a caller cannot update or disable another organization's row by
    /// guessing an id. This intentionally includes paused mappings so the same
    /// update path can re-enable them.
    /// </remarks>
    Task<Zeeq.Core.Models.CodeRepository?> FindActiveForOrganizationAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates or updates a repository mapping.
    /// </summary>
    /// <remarks>
    /// Implementations should preserve disabled historical rows and upsert only
    /// the active organization/provider/repository mapping.
    /// </remarks>
    Task<Zeeq.Core.Models.CodeRepository> UpsertAsync(
        Zeeq.Core.Models.CodeRepository repository,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Soft-disables an active repository mapping inside an organization.
    /// </summary>
    /// <remarks>
    /// This is the terminal remove-registration operation for one mapping row:
    /// historical rows are retained so prior reviews and pull request records
    /// can still point at the repository that produced them, and a later
    /// registration creates a new active row. Use <see cref="UpsertAsync"/> with
    /// <see cref="Zeeq.Core.Models.CodeRepository.Enabled"/> set to false for a reversible pause.
    /// </remarks>
    Task<bool> DisableAsync(
        string organizationId,
        string repositoryId,
        DateTimeOffset disabledAtUtc,
        CancellationToken cancellationToken
    );
}
