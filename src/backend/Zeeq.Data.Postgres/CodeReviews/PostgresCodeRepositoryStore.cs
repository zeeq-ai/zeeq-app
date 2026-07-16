using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for repositories enabled for code review workflows.
/// </summary>
/// <remarks>
/// Repository rows map a provider repository, such as a GitHub
/// <c>owner/name</c>, to the Zeeq organization and optional team that owns
/// review work. Webhook ingress uses this store to decide whether an incoming
/// repository event should create queue work or be ignored as not configured.
/// </remarks>
internal sealed class PostgresCodeRepositoryStore(PostgresDbContext db) : ICodeRepositoryStore
{
    /// <summary>
    /// Finds an enabled, non-disabled repository mapping by provider identity.
    /// </summary>
    /// <remarks>
    /// This lookup is intentionally not organization-scoped because webhook
    /// ingress starts from the provider repository identity and then resolves
    /// the owning Zeeq organization from the configured mapping.
    /// </remarks>
    public Task<CodeRepository?> FindActiveAsync(
        string provider,
        string ownerQualifiedName,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeRepositories.TagWithOperationCallSite("code_repository.find_active")
            .FirstOrDefaultAsync(
                repository =>
                    repository.Provider == provider
                    && repository.OwnerQualifiedName == ownerQualifiedName
                    && repository.DisabledAtUtc == null
                    && repository.Enabled,
                cancellationToken
            );

    /// <summary>
    /// Lists enabled repository mappings for one organization.
    /// </summary>
    /// <remarks>
    /// Paused and soft-disabled rows are excluded so workflow caches can treat
    /// the result as the actionable webhook repository set.
    /// </remarks>
    public async Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeRepositories.TagWithOperationCallSite(
                "code_repository.list_active_for_organization"
            )
            .Where(repository =>
                repository.OrganizationId == organizationId
                && repository.DisabledAtUtc == null
                && repository.Enabled
            )
            .OrderBy(repository => repository.OwnerQualifiedName)
            .ToArrayAsync(cancellationToken);

    /// <summary>
    /// Lists configured repository mappings for one organization, including paused rows.
    /// </summary>
    /// <remarks>
    /// This is the management view of repository state. It excludes historical
    /// soft-disabled rows, but it deliberately keeps <c>Enabled = false</c>
    /// rows so operators can see and re-enable paused repositories.
    /// </remarks>
    public async Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeRepositories.TagWithOperationCallSite(
                "code_repository.list_configured_for_organization"
            )
            .Where(repository =>
                repository.OrganizationId == organizationId && repository.DisabledAtUtc == null
            )
            .OrderBy(repository => repository.OwnerQualifiedName)
            .ToArrayAsync(cancellationToken);

    /// <summary>
    /// Finds one non-soft-disabled repository mapping by local id inside an organization.
    /// </summary>
    public Task<CodeRepository?> FindActiveForOrganizationAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeRepositories.TagWithOperationCallSite(
                "code_repository.find_active_for_organization"
            )
            .FirstOrDefaultAsync(
                repository =>
                    repository.OrganizationId == organizationId
                    && repository.Id == repositoryId
                    && repository.DisabledAtUtc == null,
                cancellationToken
            );

    /// <summary>
    /// Creates or updates the non-soft-disabled repository mapping for an organization/provider/name tuple.
    /// </summary>
    /// <remarks>
    /// Soft-disabled rows are intentionally not reused. If a repository is
    /// disabled and later re-added, a new active row preserves the historical
    /// disabled row while the filtered unique index protects the active mapping.
    /// </remarks>
    public async Task<CodeRepository> UpsertAsync(
        CodeRepository repository,
        CancellationToken cancellationToken
    )
    {
        // Match the filtered unique index: one active row per
        // organization/provider/owner-qualified repository name.
        var existing = await db
            .CodeRepositories.TagWithOperationCallSite("code_repository.upsert_find_existing")
            .FirstOrDefaultAsync(
                row =>
                    row.OrganizationId == repository.OrganizationId
                    && row.Provider == repository.Provider
                    && row.OwnerQualifiedName == repository.OwnerQualifiedName
                    && row.DisabledAtUtc == null,
                cancellationToken
            );

        if (existing is null)
        {
            db.CodeRepositories.Add(repository);
            await db.SaveChangesAsync(cancellationToken);
            return repository;
        }

        // Provider identity and owning organization are immutable for this active row.
        // Mutable display/configuration fields are refreshed from repository management.
        existing.TeamId = repository.TeamId;
        existing.DisplayName = repository.DisplayName;
        existing.Enabled = repository.Enabled;
        existing.LibraryIds = repository.LibraryIds;
        existing.ReviewConfiguration = repository.ReviewConfiguration;
        existing.UpdatedAtUtc = repository.UpdatedAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Soft-deletes one repository mapping so webhook ingress no longer resolves it.
    /// </summary>
    /// <remarks>
    /// This is intentionally stronger than setting <c>Enabled = false</c>.
    /// Paused mappings remain visible to management screens; soft-deleted rows
    /// are historical and can be replaced only by registering a new active row.
    /// </remarks>
    public async Task<bool> DisableAsync(
        string organizationId,
        string repositoryId,
        DateTimeOffset disabledAtUtc,
        CancellationToken cancellationToken
    )
    {
        var existing = await FindActiveForOrganizationAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );

        if (existing is null)
        {
            return false;
        }

        existing.Enabled = false;
        existing.DisabledAtUtc = disabledAtUtc;
        existing.UpdatedAtUtc = disabledAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
