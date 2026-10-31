using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Data.Postgres.Auditing;

namespace Zeeq.Data.Postgres.Documents;

// Source reconciliation and bounded sweep; retain per-document facts even when a run never finishes.
internal sealed partial class PostgresLibraryDocumentStore
{
    /// <inheritdoc />
    public Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    ) => RunDocumentMutationAsync(() => UpsertSyncedDocumentAsyncCore(document, ct), document, ct);

    private async Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsyncCore(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        if (!await LockLibraryKeyShareAsync(document.OrganizationId, document.LibraryId, ct))
            throw new InvalidOperationException("Library does not exist.");
        var result = await UpsertSyncedCoreAsync(document, ct);
        await CommitAsync(transaction, ct);
        return result;
    }

    private async Task<LibraryDocumentUpsertResult> UpsertSyncedCoreAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        // External sources (Notion) are identified by a stable id, not path — those pages are
        // renamed/reparented without their id changing, so resolving by path here would create
        // a duplicate row on every rename (spec §3.10). Skip the path/content-hash move-detection
        // branches entirely for these documents; the caller already knows the identity.
        if (document.SourceExternalId is not null)
        {
            return await UpsertSyncedExternalDocumentAsync(document, ct);
        }

        var lockedByPath = await LockByPathAsync(
            document.OrganizationId,
            document.LibraryId,
            document.Path,
            ct
        );

        if (lockedByPath is not null)
        {
            RememberDocumentPreimage(lockedByPath);
            var byPath = await TrackLockedAsync(lockedByPath, ct);
            if (lockedByPath.ContentHash == document.ContentHash)
            {
                // Content unchanged — re-stamp only. UpdatedAt intentionally does not
                // advance so unrelated observers do not see spurious churn. No audit fact:
                // nothing about the document changed.
                byPath.SyncRunId = document.SyncRunId;
                await db.SaveChangesAsync(ct);
                return new(byPath, DocumentUpsertKind.Unchanged);
            }

            var beforeFacts = LibraryDocumentAudit.SyncFacts(
                lockedByPath.Path,
                lockedByPath.Content.Length,
                null
            );
            var beforeHash = lockedByPath.ContentHash;
            byPath.Content = document.Content;
            byPath.ContentHash = document.ContentHash;
            byPath.Title = document.Title;
            byPath.TitleNormalized = document.TitleNormalized;
            byPath.ParsedSkillName = document.ParsedSkillName;
            byPath.ParsedSkillDescription = document.ParsedSkillDescription;
            byPath.Keywords = document.Keywords;
            byPath.Headings = document.Headings;
            byPath.TokenCount = document.TokenCount;
            byPath.ProcessingStatus = document.ProcessingStatus;
            byPath.SyncRunId = document.SyncRunId;
            byPath.UpdatedAt = document.UpdatedAt;
            AppendSync(
                AuditChangeType.Edited,
                byPath,
                byPath.Path,
                beforeFacts,
                LibraryDocumentAudit.SyncFacts(byPath.Path, byPath.Content.Length, null),
                ["content"],
                beforeHash,
                byPath.ContentHash
            );
            await db.SaveChangesAsync(ct);
            return new(byPath, DocumentUpsertKind.Updated);
        }

        // See PostgresDocsPublicDocumentStore.UpsertAsync's equivalent note:
        // content_hash is not unique per library, so Take(2) instead of
        // SingleOrDefaultAsync lets an ambiguous match fall through to Added
        // rather than throwing or guessing which row moved.
        var hashMatches = await LockByContentHashAsync(
            document.OrganizationId,
            document.LibraryId,
            document.ContentHash,
            ct
        );

        if (hashMatches.Length == 1)
        {
            var lockedByHash = hashMatches[0];
            RememberDocumentPreimage(lockedByHash);
            var byHash = await TrackLockedAsync(lockedByHash, ct);
            var oldPath = lockedByHash.Path;
            byHash.PreviousPaths = byHash.PreviousPaths.Contains(byHash.Path)
                ? byHash.PreviousPaths
                : [.. byHash.PreviousPaths, byHash.Path];
            byHash.Path = document.Path;
            byHash.SyncRunId = document.SyncRunId;
            byHash.UpdatedAt = document.UpdatedAt;
            AppendSync(
                AuditChangeType.Edited,
                byHash,
                byHash.Path,
                LibraryDocumentAudit.SyncFacts(oldPath, byHash.Content.Length, null),
                LibraryDocumentAudit.SyncFacts(byHash.Path, byHash.Content.Length, null),
                ["path"],
                byHash.ContentHash,
                byHash.ContentHash
            );
            await db.SaveChangesAsync(ct);
            return new(byHash, DocumentUpsertKind.Moved);
        }

        AppendSync(
            AuditChangeType.Added,
            document,
            document.Path,
            before: null,
            LibraryDocumentAudit.SyncFacts(document.Path, document.Content.Length, null),
            [],
            null,
            document.ContentHash
        );
        db.LibraryDocuments.Add(document);
        await db.SaveChangesAsync(ct);
        return new(document, DocumentUpsertKind.Added);
    }

    private async Task<LibraryDocumentUpsertResult> UpsertSyncedExternalDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        var lockedExisting = await LockByExternalIdAsync(
            document.OrganizationId,
            document.LibraryId,
            document.SourceExternalId!,
            ct
        );

        if (lockedExisting is null)
        {
            AppendSync(
                AuditChangeType.Added,
                document,
                document.Path,
                before: null,
                LibraryDocumentAudit.SyncFacts(
                    document.Path,
                    document.Content.Length,
                    document.SourceExternalId
                ),
                [],
                null,
                document.ContentHash
            );
            db.LibraryDocuments.Add(document);
            await db.SaveChangesAsync(ct);
            return new(document, DocumentUpsertKind.Added);
        }

        RememberDocumentPreimage(lockedExisting);
        var existing = await TrackLockedAsync(lockedExisting, ct);
        var oldPath = lockedExisting.Path;
        var moved = oldPath != document.Path;
        var contentChanged = lockedExisting.ContentHash != document.ContentHash;
        if (moved)
        {
            existing.RenameTo(document.Path);
        }

        if (!contentChanged && !moved)
        {
            // Content and path unchanged — re-stamp only.
            existing.SyncRunId = document.SyncRunId;
            await db.SaveChangesAsync(ct);
            return new(existing, DocumentUpsertKind.Unchanged);
        }

        var beforeHash = lockedExisting.ContentHash;
        var beforeLength = lockedExisting.Content.Length;
        existing.Content = document.Content;
        existing.ContentHash = document.ContentHash;
        existing.Title = document.Title;
        existing.TitleNormalized = document.TitleNormalized;
        existing.ParsedSkillName = document.ParsedSkillName;
        existing.ParsedSkillDescription = document.ParsedSkillDescription;
        existing.Keywords = document.Keywords;
        existing.Headings = document.Headings;
        existing.TokenCount = document.TokenCount;
        existing.ProcessingStatus = document.ProcessingStatus;
        existing.SyncRunId = document.SyncRunId;
        existing.UpdatedAt = document.UpdatedAt;

        var changedFields = new List<string>(2);
        if (moved)
        {
            changedFields.Add("path");
        }

        if (contentChanged)
        {
            changedFields.Add("content");
        }

        AppendSync(
            AuditChangeType.Edited,
            existing,
            existing.Path,
            LibraryDocumentAudit.SyncFacts(oldPath, beforeLength, existing.SourceExternalId),
            LibraryDocumentAudit.SyncFacts(
                existing.Path,
                existing.Content.Length,
                existing.SourceExternalId
            ),
            changedFields,
            beforeHash,
            existing.ContentHash
        );
        await db.SaveChangesAsync(ct);
        return new(existing, moved ? DocumentUpsertKind.Moved : DocumentUpsertKind.Updated);
    }

    private void AppendSync(
        AuditChangeType changeType,
        LibraryDocument subject,
        string subjectPath,
        AuditSnapshot? before,
        AuditSnapshot? after,
        IReadOnlyList<string> changedFields,
        string? beforeHash,
        string? afterHash
    ) =>
        audit.AppendOrganization(
            subject.OrganizationId,
            subject.TeamId,
            LibraryDocumentAudit.Sync(
                changeType,
                subject,
                subjectPath,
                subject.SyncRunId,
                before,
                after,
                changedFields,
                beforeHash,
                afterHash
            )
        );

    /// <inheritdoc />
    public Task<LibraryDocument?> GetByExternalIdAsync(
        string organizationId,
        string libraryId,
        string sourceExternalId,
        CancellationToken ct
    ) =>
        db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.get_by_external_id"
            )
            .SingleOrDefaultAsync(
                row =>
                    row.OrganizationId == organizationId
                    && row.LibraryId == libraryId
                    && row.SourceExternalId == sourceExternalId,
                ct
            );

    /// <inheritdoc />
    public async Task<int> DeleteUnstampedAsync(
        string organizationId,
        string libraryId,
        string currentSyncRunId,
        CancellationToken ct
    )
    {
        var ambient = db.Database.CurrentTransaction;
        // Save the caller's pending work before creating the sweep savepoint. Otherwise a batch's
        // SaveChanges would mark that work unchanged even if a later batch rolls back its insert.
        // This flush stays inside the caller's transaction; it does not commit the caller's work.
        Zeeq.Data.Postgres.CodeReviews.RepositoryLinkGuard.RejectPending(db);
        if (ambient is not null && db.ChangeTracker.HasChanges())
        {
            // Lock an existing parent before pending child writes. A new parent cannot be
            // locked yet and cannot have competing committed child rows.
            var isNew = db
                .ChangeTracker.Entries<Library>()
                .Any(entry =>
                    entry.Entity.OrganizationId == organizationId
                    && entry.Entity.Id == libraryId
                    && entry.State == EntityState.Added
                );
            if (!isNew)
                await LockLibraryKeyShareAsync(organizationId, libraryId, ct);
            await db.SaveChangesAsync(ct);
        }

        await using var transaction = await BeginOwnedTransactionAsync(ct);
        var savepoint = $"audit_sweep_{Guid.NewGuid():N}";
        if (ambient is not null)
        {
            await ambient.CreateSavepointAsync(savepoint, ct);
        }
        var existingFacts = db
            .ChangeTracker.Entries<OrganizationAuditEvent>()
            .Select(entry => entry.Entity)
            .ToHashSet();
        try
        {
            if (!await LockLibraryKeyShareAsync(organizationId, libraryId, ct))
            {
                if (ambient is not null)
                    await ambient.ReleaseSavepointAsync(savepoint, ct);
                await CommitAsync(transaction, ct);
                return 0;
            }

            // Hold the caller's transaction for the whole sweep, but keep each locked result and
            // its newly tracked facts bounded. Waiting for locks (without SKIP LOCKED) ensures that
            // a concurrent re-stamp is rechecked by PostgreSQL before a row can be deleted.
            // Batching bounds client memory, not total lock duration or WAL. The ID cursor defines
            // this pass; rows inserted behind it are not revisited as if this were a frozen snapshot.
            const int batchSize = 200;
            var totalDeleted = 0;
            string? lastId = null;
            while (true)
            {
                var candidates = await db
                    .Database.SqlQuery<UnstampedDocumentRow>(
                        $"""
                        SELECT id, path, content_hash, team_id, source_external_id
                        FROM zeeq.docs_library_documents
                        WHERE organization_id = {organizationId}
                          AND library_id = {libraryId}
                          AND sync_run_id IS DISTINCT FROM {currentSyncRunId}
                          AND ({lastId}::text IS NULL OR id > {lastId})
                        ORDER BY id
                        LIMIT {batchSize}
                        FOR UPDATE
                        """
                    )
                    .ToArrayAsync(ct);
                if (candidates.Length == 0)
                {
                    break;
                }

                var ids = Array.ConvertAll(candidates, static candidate => candidate.Id);
                totalDeleted += await db
                    .LibraryDocuments.TagWithOperationCallSite(
                        "documents.library_document.delete_unstamped"
                    )
                    .Where(document =>
                        document.OrganizationId == organizationId
                        && document.LibraryId == libraryId
                        && ids.Contains(document.Id)
                    )
                    .ExecuteDeleteAsync(ct);

                var previouslyTracked = db
                    .ChangeTracker.Entries<OrganizationAuditEvent>()
                    .Select(entry => entry.Entity)
                    .ToHashSet();
                foreach (var candidate in candidates)
                {
                    audit.AppendOrganization(
                        organizationId,
                        candidate.TeamId,
                        new AuditChange
                        {
                            EventType = LibraryDocumentAudit.Synced,
                            ChangeType = AuditChangeType.Deleted,
                            Area = AuditArea.Content,
                            SubjectType = "document",
                            SubjectId = candidate.Id,
                            SubjectLabel = candidate.Path,
                            LibraryId = libraryId,
                            Before = LibraryDocumentAudit.SyncFacts(
                                candidate.Path,
                                null,
                                candidate.SourceExternalId
                            ),
                            BeforeHash = candidate.ContentHash,
                            ActorOverride = AuditActor.Worker("Content sync"),
                            OperationId = LibraryDocumentAudit.SyncOperationId(currentSyncRunId),
                            SourceReference = LibraryDocumentAudit.SyncSourceReference(
                                currentSyncRunId,
                                candidate.SourceExternalId
                            ),
                        }
                    );
                }

                var newFacts = db
                    .ChangeTracker.Entries<OrganizationAuditEvent>()
                    .Where(entry => !previouslyTracked.Contains(entry.Entity))
                    .ToArray();
                await db.SaveChangesAsync(ct);
                foreach (var entry in newFacts)
                {
                    entry.State = EntityState.Detached;
                }
                lastId = candidates[^1].Id;
            }

            if (ambient is not null)
            {
                await ambient.ReleaseSavepointAsync(savepoint, ct);
            }
            await CommitAsync(transaction, ct);
            return totalDeleted;
        }
        catch
        {
            try
            {
                if (ambient is not null)
                {
                    // A canceled request must still undo already-applied batches before the caller
                    // catches the failure and continues using its transaction.
                    await ambient.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                }
            }
            finally
            {
                // A writer may throw after attaching some facts in the current batch.
                // Never detach entities that belonged to the caller before this sweep.
                foreach (
                    var entry in db
                        .ChangeTracker.Entries<OrganizationAuditEvent>()
                        .Where(entry => !existingFacts.Contains(entry.Entity))
                        .ToArray()
                )
                {
                    entry.State = EntityState.Detached;
                }
            }
            throw;
        }
    }

    private sealed class UnstampedDocumentRow
    {
        public string Id { get; init; } = null!;
        public string Path { get; init; } = null!;
        public string? ContentHash { get; init; }
        public string? TeamId { get; init; }
        public string? SourceExternalId { get; init; }
    }
}
