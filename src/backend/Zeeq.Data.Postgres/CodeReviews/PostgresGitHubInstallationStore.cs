using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres implementation of GitHub App installation persistence.
/// </summary>
/// <remarks>
/// The GitHub installation id is globally unique for the GitHub App and is the
/// durable key for webhook/token resolution. The Zeeq organization/team link
/// is the local ownership boundary; this store rejects incidental relinks so
/// reassignment can be handled by an explicit audited flow later.
/// </remarks>
internal sealed class PostgresGitHubInstallationStore(PostgresDbContext db)
    : IGitHubInstallationStore
{
    /// <summary>
    /// Finds the active local row for a GitHub installation id.
    /// </summary>
    /// <remarks>
    /// Webhook and token-resolution paths use this lookup after GitHub has
    /// supplied an installation id. Deleted/disabled rows are ignored so
    /// downstream callers fail closed when an installation is no longer usable.
    /// </remarks>
    public Task<GitHubAppInstallation?> FindByInstallationIdAsync(
        long installationId,
        CancellationToken cancellationToken
    ) =>
        db
            .GitHubAppInstallations.TagWithOperationCallSite(
                "github_installation.find_by_installation_id"
            )
            .FirstOrDefaultAsync(
                installation =>
                    installation.InstallationId == installationId
                    && installation.DisabledAtUtc == null
                    && installation.DeletedAtUtc == null,
                cancellationToken
            );

    /// <summary>
    /// Finds the newest active GitHub installation linked to an organization.
    /// </summary>
    /// <remarks>
    /// This supports organization-scoped UI and future client factories that
    /// need to resolve an installation before asking GitHub for an installation
    /// access token.
    /// </remarks>
    public Task<GitHubAppInstallation?> FindActiveForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    ) =>
        db
            .GitHubAppInstallations.TagWithOperationCallSite(
                "github_installation.find_active_for_organization"
            )
            .Where(installation =>
                installation.OrganizationId == organizationId
                && installation.DisabledAtUtc == null
                && installation.DeletedAtUtc == null
            )
            .OrderByDescending(installation => installation.InstalledAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Inserts a newly linked installation or refreshes GitHub metadata for an existing link.
    /// </summary>
    /// <remarks>
    /// Existing rows are matched by GitHub installation id. Updates refresh
    /// GitHub-owned metadata only when the callback state matches the existing
    /// local owner. A future reassignment flow must be explicit and audited
    /// because this table is the canonical local mapping.
    /// </remarks>
    public async Task<GitHubAppInstallation> UpsertLinkedInstallationAsync(
        GitHubAppInstallation installation,
        CancellationToken cancellationToken
    )
    {
        var existing = await db
            .GitHubAppInstallations.TagWithOperationCallSite(
                "github_installation.upsert_find_existing"
            )
            .FirstOrDefaultAsync(
                row => row.InstallationId == installation.InstallationId,
                cancellationToken
            );

        if (existing is null)
        {
            db.GitHubAppInstallations.Add(installation);
            await db.SaveChangesAsync(cancellationToken);
            return installation;
        }

        if (
            existing.OrganizationId != installation.OrganizationId
            || existing.TeamId != installation.TeamId
        )
        {
            throw new GitHubInstallationLinkConflictException(
                installation.InstallationId,
                existing.OrganizationId,
                existing.TeamId,
                installation.OrganizationId,
                installation.TeamId
            );
        }

        // Repeated callbacks for the same local owner refresh GitHub metadata
        // only. Do not mutate OrganizationId/TeamId here without adding an
        // explicit cross-organization reassignment policy.
        existing.AccountLogin = installation.AccountLogin;
        existing.AccountId = installation.AccountId;
        existing.AccountType = installation.AccountType;
        existing.RepositorySelection = installation.RepositorySelection;
        existing.RawInstallationJson = installation.RawInstallationJson;
        existing.SuspendedAtUtc = installation.SuspendedAtUtc;
        existing.DeletedAtUtc = installation.DeletedAtUtc;
        existing.DisabledAtUtc = null;
        existing.UpdatedAtUtc = installation.UpdatedAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Applies a lifecycle change reported by an installation webhook event.
    /// </summary>
    /// <remarks>
    /// Matches by GitHub installation id only, independent of the row's current
    /// disabled/deleted flags, so a delivery can reconcile state regardless of
    /// what was last known locally. No-ops when the installation has no linked
    /// row; the browser install callback owns first-link creation.
    /// </remarks>
    public async Task ApplyLifecycleEventAsync(
        long installationId,
        string repositorySelection,
        DateTimeOffset? suspendedAtUtc,
        DateTimeOffset? deletedAtUtc,
        CancellationToken cancellationToken
    )
    {
        var existing = await db
            .GitHubAppInstallations.TagWithOperationCallSite(
                "github_installation.apply_lifecycle_event"
            )
            .FirstOrDefaultAsync(row => row.InstallationId == installationId, cancellationToken);

        if (existing is null)
        {
            return;
        }

        // NOTE: Once a row is recorded as deleted, treat it as permanently dead.
        // GitHub never reuses installation ids after deletion, so no legitimate
        // later delivery should change this row again. This also guards against
        // an out-of-order/retried Suspend, Unsuspend, or NewPermissionsAccepted
        // delivery arriving after Deleted and clobbering DeletedAtUtc back to null.
        if (existing.DeletedAtUtc is not null)
        {
            return;
        }

        existing.RepositorySelection = repositorySelection;
        existing.SuspendedAtUtc = suspendedAtUtc;
        existing.DeletedAtUtc = deletedAtUtc;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }
}
