using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Postgres-backed store for the external-content dirty set.
/// </summary>
internal sealed class PostgresExternalPendingContentSyncStore(PostgresDbContext db)
    : IExternalPendingContentSyncStore
{
    /// <inheritdoc />
    public async Task UpsertAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        string eventType,
        DateTimeOffset markedDirtyAtUtc,
        CancellationToken ct
    ) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO zeeq.docs_external_pending_content_syncs
                (organization_id, library_id, external_content_id, event_type, marked_dirty_at_utc)
            VALUES ({organizationId}, {libraryId}, {externalContentId}, {eventType}, {markedDirtyAtUtc})
            ON CONFLICT (organization_id, library_id, external_content_id) DO UPDATE
            SET event_type = EXCLUDED.event_type,
                marked_dirty_at_utc = EXCLUDED.marked_dirty_at_utc,
                -- Reset the claim so an item re-dirtied mid-run is NOT cleared by that
                -- run's compare-and-delete in ClearAsync; it survives for the next run.
                claimed_by_run_id = NULL,
                claimed_at_utc = NULL
            """,
            ct
        );

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalPendingContentSync>> ClaimAsync(
        string organizationId,
        string libraryId,
        string runId,
        DateTimeOffset claimedAtUtc,
        TimeSpan staleClaimAfter,
        CancellationToken ct
    )
    {
        var staleClaimCutoff = claimedAtUtc - staleClaimAfter;

        // A single atomic UPDATE ... RETURNING, rather than a SELECT FOR UPDATE followed by a
        // tracked SaveChangesAsync: this table is mutated out-of-band by UpsertAsync's raw SQL
        // (bypassing the change tracker), so any row already tracked in this DbContext from an
        // earlier query would keep its stale in-memory values under EF's default identity-map
        // merge behavior. AsNoTracking sidesteps that entirely — every claim result here is
        // read fresh from what the UPDATE actually wrote.
        //
        // The "OR claimed_at_utc < staleClaimCutoff" arm reclaims rows left behind by a run that
        // crashed or was killed after claiming but before ClearAsync — otherwise those rows have
        // no path back to "claimable" short of another webhook re-dirtying the same item.
        var claimed = await db
            .ExternalPendingContentSyncs.FromSql(
                $"""
                UPDATE zeeq.docs_external_pending_content_syncs
                SET claimed_by_run_id = {runId}, claimed_at_utc = {claimedAtUtc}
                WHERE (organization_id, library_id, external_content_id) IN (
                    SELECT organization_id, library_id, external_content_id
                    FROM zeeq.docs_external_pending_content_syncs
                    WHERE organization_id = {organizationId}
                      AND library_id = {libraryId}
                      AND (claimed_by_run_id IS NULL OR claimed_at_utc < {staleClaimCutoff})
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING *
                """
            )
            .AsNoTracking()
            .TagWithOperationCallSite("documents.external_pending_content_sync.claim")
            .ToArrayAsync(ct);

        return
        [
            .. claimed.Select(row => new ExternalPendingContentSync(
                row.ExternalContentId,
                row.EventType
            )),
        ];
    }

    /// <inheritdoc />
    public async Task ClearAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        string runId,
        CancellationToken ct
    ) =>
        await db
            .ExternalPendingContentSyncs.TagWithOperationCallSite(
                "documents.external_pending_content_sync.clear"
            )
            .Where(row =>
                row.OrganizationId == organizationId
                && row.LibraryId == libraryId
                && row.ExternalContentId == externalContentId
                && row.ClaimedByRunId == runId
            )
            .ExecuteDeleteAsync(ct);

    /// <inheritdoc />
    public async Task RemoveAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        CancellationToken ct
    ) =>
        await db
            .ExternalPendingContentSyncs.TagWithOperationCallSite(
                "documents.external_pending_content_sync.remove"
            )
            .Where(row =>
                row.OrganizationId == organizationId
                && row.LibraryId == libraryId
                && row.ExternalContentId == externalContentId
            )
            .ExecuteDeleteAsync(ct);

    /// <inheritdoc />
    public async Task<bool> IsClaimActiveAsync(
        string organizationId,
        string libraryId,
        string externalContentId,
        string runId,
        CancellationToken ct
    ) =>
        await db
            .ExternalPendingContentSyncs.AsNoTracking()
            .TagWithOperationCallSite("documents.external_pending_content_sync.is_claim_active")
            .AnyAsync(
                row =>
                    row.OrganizationId == organizationId
                    && row.LibraryId == libraryId
                    && row.ExternalContentId == externalContentId
                    && row.ClaimedByRunId == runId,
                ct
            );
}
