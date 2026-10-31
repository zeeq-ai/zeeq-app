using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Auditing;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Data.Postgres.Auditing;

namespace Zeeq.Data.Postgres.Identity;

/// <inheritdoc cref="IOrganizationActivationKeyStore" />
internal sealed class PostgresOrganizationActivationKeyStore(
    PostgresDbContext db,
    IAuditTrailWriter? auditWriter = null
) : IOrganizationActivationKeyStore
{
    private readonly IAuditTrailWriter audit =
        auditWriter ?? new PostgresAuditTrailWriter(db, new AuditActorContext());

    /// <inheritdoc />
    public async Task<OrganizationActivationKey> CreateKeyAsync(
        OrganizationActivationKey key,
        CancellationToken ct
    )
    {
        try
        {
            return await AuditMutationCheckpoint.RunAsync(
                db,
                async () =>
                {
                    db.OrganizationActivationKeys.Add(key);
                    audit.AppendSystem(
                        KeyChange(
                            key,
                            eventType: "activation_key.created",
                            changeType: AuditChangeType.Added,
                            beforeStatus: null,
                            afterStatus: "Available"
                        )
                    );
                    await db.SaveChangesAsync(ct);

                    return key;
                },
                ct
            );
        }
        catch
        {
            // The attempted create is rejected even if the caller pre-attached this key.
            // Preserve unrelated Added entries, but do not replay this key without its fact.
            var entry = db.Entry(key);
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<
        OrganizationActivationKeyPage<OrganizationActivationKeySummary>
    > ListKeysAsync(
        int page,
        int pageSize,
        string? query,
        OrganizationActivationKeyStatus? status,
        CancellationToken ct
    )
    {
        ValidatePagination(page, pageSize);

        var now = DateTimeOffset.UtcNow;
        var normalizedQuery = NormalizeQuery(query);
        var keysQuery =
            from key in db
                .OrganizationActivationKeys.TagWithOperationCallSite("activation_keys.list")
                .AsNoTracking()
            join user in db.Users.AsNoTracking() on key.CreatedByUserId equals user.Id into creators
            from creator in creators.DefaultIfEmpty()
            select new
            {
                Key = key,
                CreatedByDisplayName = creator == null ? key.CreatedByUserId : creator.DisplayName,
            };

        if (normalizedQuery is not null)
        {
            // NOTE: Activation-key volume is intentionally low. Accept the
            // function-wrapped substring scan until usage justifies pg_trgm or
            // a prefix-search contract.
            keysQuery = keysQuery.Where(row =>
                row.Key.Id.ToLower().Contains(normalizedQuery)
                || row.Key.CreatedByUserId.ToLower().Contains(normalizedQuery)
                || row.CreatedByDisplayName.ToLower().Contains(normalizedQuery)
                || (row.Key.Note != null && row.Key.Note.ToLower().Contains(normalizedQuery))
                || (
                    row.Key.ActivatedOrganizationId != null
                    && row.Key.ActivatedOrganizationId.ToLower().Contains(normalizedQuery)
                )
            );
        }

        if (status is { } requestedStatus)
        {
            keysQuery = requestedStatus switch
            {
                OrganizationActivationKeyStatus.Available => keysQuery.Where(row =>
                    row.Key.ActivatedAtUtc == null
                    && row.Key.DisabledAtUtc == null
                    && row.Key.ExpiresAtUtc > now
                ),
                OrganizationActivationKeyStatus.Activated => keysQuery.Where(row =>
                    row.Key.ActivatedAtUtc != null
                ),
                OrganizationActivationKeyStatus.Revoked => keysQuery.Where(row =>
                    row.Key.ActivatedAtUtc == null && row.Key.DisabledAtUtc != null
                ),
                OrganizationActivationKeyStatus.Expired => keysQuery.Where(row =>
                    row.Key.ActivatedAtUtc == null
                    && row.Key.DisabledAtUtc == null
                    && row.Key.ExpiresAtUtc <= now
                ),
                _ => keysQuery,
            };
        }

        var totalCount = await keysQuery.CountAsync(ct);

        var items = await keysQuery
            .OrderByDescending(row => row.Key.CreatedAtUtc)
            .ThenBy(row => row.Key.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => ToSummary(row.Key, row.CreatedByDisplayName, now))
            .ToArrayAsync(ct);

        return new OrganizationActivationKeyPage<OrganizationActivationKeySummary>(
            items,
            page,
            pageSize,
            totalCount
        );
    }

    /// <inheritdoc />
    public async Task<OrganizationActivationKeySummary?> RevokeKeyAsync(
        string keyId,
        CancellationToken ct
    )
    {
        return await AuditMutationCheckpoint.RunAsync<OrganizationActivationKeySummary?>(
            db,
            async () =>
            {
                var before = await db
                    .OrganizationActivationKeys.FromSqlInterpolated(
                        $"""
                        SELECT * FROM zeeq.core_organization_activation_keys
                        WHERE id = {keyId} AND activated_at_utc IS NULL AND disabled_at_utc IS NULL
                        FOR UPDATE
                        """
                    )
                    .TagWithOperationCallSite("activation_keys.revoke_preimage")
                    .AsNoTracking()
                    .SingleOrDefaultAsync(ct);
                if (before is null)
                {
                    return null;
                }

                var now = DateTimeOffset.UtcNow;
                var revoked = await db
                    .OrganizationActivationKeys.TagWithOperationCallSite("activation_keys.revoke")
                    .Where(key =>
                        key.Id == keyId && key.ActivatedAtUtc == null && key.DisabledAtUtc == null
                    )
                    .ExecuteUpdateAsync(
                        setters =>
                            setters
                                .SetProperty(key => key.DisabledAtUtc, now)
                                .SetProperty(key => key.UpdatedAtUtc, now),
                        ct
                    );

                if (revoked != 1)
                {
                    return null;
                }

                audit.AppendSystem(
                    KeyChange(
                        before,
                        eventType: "activation_key.revoked",
                        changeType: AuditChangeType.Edited,
                        beforeStatus: before.ExpiresAtUtc <= now ? "Expired" : "Available",
                        afterStatus: "Revoked"
                    )
                );
                await db.SaveChangesAsync(ct);

                var row = await (
                    from key in db
                        .OrganizationActivationKeys.TagWithOperationCallSite(
                            "activation_keys.revoke_read"
                        )
                        .AsNoTracking()
                    join user in db.Users.AsNoTracking()
                        on key.CreatedByUserId equals user.Id
                        into creators
                    from creator in creators.DefaultIfEmpty()
                    where key.Id == keyId
                    select new
                    {
                        Key = key,
                        CreatedByDisplayName = creator == null
                            ? key.CreatedByUserId
                            : creator.DisplayName,
                    }
                ).SingleAsync(ct);

                return ToSummary(row.Key, row.CreatedByDisplayName, now);
            },
            ct
        );
    }

    /// <summary>
    /// Claims one activation key and activates one eligible organization.
    /// </summary>
    /// <remarks>
    /// This method uses Postgres-backed set-based updates rather than loading
    /// entities into the EF change tracker. The key claim and organization
    /// activation predicates live inside <c>UPDATE</c> statements so concurrent
    /// exchanges cannot both observe a key as available.
    ///
    /// The operation is transactional. If the caller already owns a transaction,
    /// the method uses a savepoint so a failed organization activation rolls
    /// back only the key claim performed here. Without an ambient transaction,
    /// the method owns and commits or rolls back its local transaction.
    /// </remarks>
    public async Task<OrganizationActivationExchangeResult> ConsumeKeyAndActivateOrganizationAsync(
        string keyHash,
        string organizationId,
        string userId,
        CancellationToken ct
    )
    {
        // Reuse ambient transactions from tests or higher-level workflows; otherwise
        // own the transaction so key claim and org activation commit together.
        var existingTransaction = db.Database.CurrentTransaction;

        await using var transaction = existingTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        // Savepoints keep this method composable inside an existing transaction:
        // a later org rejection can undo the key claim without aborting the caller.
        var savepointName = existingTransaction is null
            ? null
            : "activation_" + Guid.NewGuid().ToString("N");

        if (savepointName is not null)
        {
            await existingTransaction!.CreateSavepointAsync(savepointName, ct);
        }

        var now = DateTimeOffset.UtcNow;
        var operationId = Guid.CreateVersion7();

        try
        {
            var organizationBefore = await db
                .Organizations.FromSqlInterpolated(
                    $"SELECT * FROM zeeq.core_organizations WHERE id = {organizationId} FOR UPDATE"
                )
                .TagWithOperationCallSite("activation_keys.consume_org_preimage")
                .AsNoTracking()
                .SingleOrDefaultAsync(ct);

            // Establish org eligibility before writing activation foreign keys to
            // the key row; missing org ids then return InvalidOrganization, not FK errors.
            var activated = await db
                .Organizations.TagWithOperationCallSite("activation_keys.consume_org")
                .Where(organization =>
                    organization.Id == organizationId
                    && organization.CreatedByUserId == userId
                    && organization.DisabledAtUtc == null
                    && organization.ActivatedAtUtc == null
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(organization => organization.ActivatedAtUtc, now)
                            .SetProperty(organization => organization.UpdatedAtUtc, now),
                    ct
                );

            if (activated != 1)
            {
                await RollbackActivationAttemptAsync(
                    transaction,
                    existingTransaction,
                    savepointName,
                    CancellationToken.None // Explicit none so cancellation doesn't leave a half-activated org with a claimed key.
                );

                return OrganizationActivationExchangeResult.InvalidOrganization;
            }

            // Claim the key after org eligibility is established. If the key is
            // unavailable, rollback the org activation so the exchange stays atomic.
            var claimed = await db
                .OrganizationActivationKeys.TagWithOperationCallSite("activation_keys.consume_key")
                .Where(key =>
                    key.KeyHash == keyHash
                    && key.DisabledAtUtc == null
                    && key.ActivatedAtUtc == null
                    && key.ExpiresAtUtc > now
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(key => key.ActivatedAtUtc, now)
                            .SetProperty(key => key.ActivatedOrganizationId, organizationId)
                            .SetProperty(key => key.ActivatedByUserId, userId)
                            .SetProperty(key => key.UpdatedAtUtc, now),
                    ct
                );

            if (claimed != 1)
            {
                await RollbackActivationAttemptAsync(
                    transaction,
                    existingTransaction,
                    savepointName,
                    CancellationToken.None
                );

                return OrganizationActivationExchangeResult.InvalidKey;
            }

            var key = await db
                .OrganizationActivationKeys.TagWithOperationCallSite(
                    "activation_keys.consume_audit_metadata"
                )
                .AsNoTracking()
                .SingleAsync(candidate => candidate.KeyHash == keyHash, ct);
            audit.AppendSystem(
                KeyChange(
                    key,
                    eventType: "activation_key.redeemed",
                    changeType: AuditChangeType.Edited,
                    beforeStatus: "Available",
                    afterStatus: "Activated"
                ) with
                {
                    OperationId = operationId,
                }
            );
            audit.AppendOrganization(
                organizationId: organizationId,
                teamId: null,
                change: new()
                {
                    EventType = "organization.activated",
                    ChangeType = AuditChangeType.Edited,
                    Area = AuditArea.Settings,
                    SubjectType = "organization",
                    SubjectId = organizationId,
                    SubjectLabel = organizationBefore!.DisplayName ?? organizationId,
                    OrganizationLabel = organizationBefore.DisplayName,
                    OperationId = operationId,
                    Before = new()
                    {
                        Fields = new Dictionary<string, string?> { ["Active"] = "false" },
                    },
                    After = new()
                    {
                        Fields = new Dictionary<string, string?> { ["Active"] = "true" },
                    },
                    ChangedFields = ["Active"],
                }
            );
            await db.SaveChangesAsync(ct);

            // Both updates succeeded: either commit our transaction or release the
            // savepoint and leave final commit ownership with the ambient caller.
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await existingTransaction!.ReleaseSavepointAsync(savepointName!, ct);
            }
        }
        catch
        {
            // Any exception after the savepoint/transaction starts must undo
            // partial activation work before the ambient caller can commit.
            await RollbackActivationAttemptAsync(
                transaction,
                existingTransaction,
                savepointName,
                CancellationToken.None
            );
            // A savepoint rolls back SQL, but not the change tracker. Remove only this
            // attempt's facts so a caller that continues cannot save a phantom redemption.
            foreach (
                var entry in db
                    .ChangeTracker.Entries<OrganizationAuditEvent>()
                    .Where(entry => entry.Entity.OperationId == operationId)
                    .ToArray()
            )
            {
                entry.State = EntityState.Detached;
            }
            foreach (
                var entry in db
                    .ChangeTracker.Entries<SystemAuditEvent>()
                    .Where(entry => entry.Entity.OperationId == operationId)
                    .ToArray()
            )
            {
                entry.State = EntityState.Detached;
            }
            throw;
        }

        return OrganizationActivationExchangeResult.Activated;
    }

    private static AuditChange KeyChange(
        OrganizationActivationKey key,
        string eventType,
        AuditChangeType changeType,
        string? beforeStatus,
        string afterStatus
    ) =>
        new()
        {
            EventType = eventType,
            ChangeType = changeType,
            Area = AuditArea.Security,
            SubjectType = "activation_key",
            SubjectId = key.Id,
            // Notes are private free text and key hashes are secret-derived material.
            // Neither is copied into the history envelope or snapshots.
            SubjectLabel = key.Id,
            Before = beforeStatus is null ? null : KeySnapshot(key, beforeStatus),
            After = KeySnapshot(key, afterStatus),
            ChangedFields = beforeStatus is null ? ["Status", "ExpiresAtUtc"] : ["Status"],
        };

    private static AuditSnapshot KeySnapshot(OrganizationActivationKey key, string status) =>
        new()
        {
            Fields = new Dictionary<string, string?>
            {
                ["Status"] = status,
                ["ExpiresAtUtc"] = key.ExpiresAtUtc.ToString("O"),
            },
        };

    private static async Task RollbackActivationAttemptAsync(
        IDbContextTransaction? transaction,
        IDbContextTransaction? existingTransaction,
        string? savepointName,
        CancellationToken ct
    )
    {
        // NOTE: Callers pass CancellationToken.None once activation work has
        // started. Rollback restores the atomicity invariant and must still run
        // after a cancelled key-claim query.
        if (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            return;
        }

        if (savepointName is not null)
        {
            await existingTransaction!.RollbackToSavepointAsync(savepointName, ct);
            try
            {
                await existingTransaction.ReleaseSavepointAsync(savepointName!, ct);
            }
            catch (InvalidOperationException)
            {
                // NOTE: Some providers treat rollback-to-savepoint as clearing
                // the savepoint marker. The rollback is the required invariant.
            }
        }
    }

    private static void ValidatePagination(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
    }

    private static string? NormalizeQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : query.Trim().ToLowerInvariant();

    private static OrganizationActivationKeySummary ToSummary(
        OrganizationActivationKey key,
        string createdByDisplayName,
        DateTimeOffset now
    ) =>
        new(
            key.Id,
            key.Note,
            key.CreatedByUserId,
            createdByDisplayName,
            key.CreatedAtUtc,
            key.UpdatedAtUtc,
            key.ExpiresAtUtc,
            key.ActivatedAtUtc,
            key.ActivatedOrganizationId,
            key.ActivatedByUserId,
            key.DisabledAtUtc,
            ComputeStatus(key, now)
        );

    private static OrganizationActivationKeyStatus ComputeStatus(
        OrganizationActivationKey key,
        DateTimeOffset now
    ) =>
        key switch
        {
            { ActivatedAtUtc: not null } => OrganizationActivationKeyStatus.Activated,
            { DisabledAtUtc: not null } => OrganizationActivationKeyStatus.Revoked,
            { ExpiresAtUtc: var expiresAtUtc } when expiresAtUtc <= now =>
                OrganizationActivationKeyStatus.Expired,
            _ => OrganizationActivationKeyStatus.Available,
        };
}
