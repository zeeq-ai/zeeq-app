using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

// Direct document writes; keep the locked preimage, mutation and explicit audit append in one transaction.
internal sealed partial class PostgresLibraryDocumentStore
{
    /// <inheritdoc />
    public Task<LibraryDocument> UpsertDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    ) => RunDocumentMutationAsync(() => UpsertDocumentAsyncCore(document, ct), document, ct);

    /// <inheritdoc />
    public async Task DeleteDocumentAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) =>
        await RunDocumentMutationAsync(
            async () =>
            {
                await DeleteDocumentAsyncCore(organizationId, libraryId, path, ct);
                return true;
            },
            null,
            ct
        );

    /// <inheritdoc />
    public async Task DeleteDocumentByExternalIdAsync(
        string organizationId,
        string libraryId,
        string sourceExternalId,
        CancellationToken ct
    ) =>
        await RunDocumentMutationAsync(
            async () =>
            {
                await DeleteDocumentByExternalIdAsyncCore(
                    organizationId,
                    libraryId,
                    sourceExternalId,
                    ct
                );
                return true;
            },
            null,
            ct
        );

    /// <inheritdoc />
    public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
        string organizationId,
        string libraryId,
        string documentId,
        bool excluded,
        CancellationToken ct
    ) =>
        RunDocumentMutationAsync(
            () =>
                SetCodeReviewExclusionAsyncCore(
                    organizationId,
                    libraryId,
                    documentId,
                    excluded,
                    ct
                ),
            null,
            ct
        );

    /// <inheritdoc />
    public Task<LibraryDocument?> SetScopedSkillAsync(
        string organizationId,
        string libraryId,
        string documentId,
        LibraryDocumentScopedSkill scopedSkill,
        CancellationToken ct
    ) =>
        RunDocumentMutationAsync(
            () => SetScopedSkillAsyncCore(organizationId, libraryId, documentId, scopedSkill, ct),
            null,
            ct
        );

    /// <inheritdoc />
    public Task<LibraryDocument?> MoveDocumentAsync(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath,
        CancellationToken ct
    ) =>
        RunDocumentMutationAsync(
            () => MoveDocumentAsyncCore(organizationId, libraryId, fromPath, toPath, ct),
            null,
            ct
        );

    private async Task<LibraryDocument> UpsertDocumentAsyncCore(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        if (!await LockLibraryKeyShareAsync(document.OrganizationId, document.LibraryId, ct))
            throw new InvalidOperationException("Library does not exist.");

        // NOTE: The write service may already have mutated its own copy of this document before it
        // calls the store, so the "before" state comes from the locked database row, not from
        // anything the caller holds. Locking also serializes concurrent edits of the same path.
        var locked = await LockByPathAsync(
            document.OrganizationId,
            document.LibraryId,
            document.Path,
            ct
        );

        if (locked is null)
        {
            var sourced = await IsSourcedAsync(document, ct);
            audit.AppendOrganization(
                document.OrganizationId,
                document.TeamId,
                LibraryDocumentAudit.Local(
                    LibraryDocumentAudit.Created,
                    AuditChangeType.Added,
                    AuditArea.Content,
                    document,
                    document.Path,
                    before: null,
                    after: SnapshotForAudit(document, sourced),
                    changedFields: [],
                    beforeHash: null,
                    afterHash: document.ContentHash
                )
            );
            db.LibraryDocuments.Add(document);
            await db.SaveChangesAsync(ct);
            await CommitAsync(transaction, ct);
            return document;
        }

        RememberDocumentPreimage(locked);
        var contentChanged = !string.Equals(
            locked.Content,
            document.Content,
            StringComparison.Ordinal
        );
        var titleChanged = !string.Equals(locked.Title, document.Title, StringComparison.Ordinal);
        // The caller can pass the tracked entity itself. Reloading it to obtain the current
        // database state must not discard the values that the caller asked us to write.
        var requested = (
            document.Title,
            document.TitleNormalized,
            document.ParsedSkillName,
            document.ParsedSkillDescription,
            document.Keywords,
            document.Headings,
            document.Content,
            document.ProcessingStatus,
            document.TokenCount,
            document.ContentHash,
            document.UpdatedAt
        );
        var existing = await TrackLockedAsync(locked, ct);
        var sourcedUpdate = await IsSourcedAsync(locked, ct);
        var beforeSnapshot = SnapshotForAudit(locked, sourcedUpdate);
        var beforeHash = locked.ContentHash;
        var changedFields = new List<string>(2);
        if (contentChanged)
        {
            changedFields.Add("content");
        }

        if (titleChanged)
        {
            changedFields.Add("title");
        }

        existing.Title = requested.Title;
        existing.TitleNormalized = requested.TitleNormalized;
        existing.ParsedSkillName = requested.ParsedSkillName;
        existing.ParsedSkillDescription = requested.ParsedSkillDescription;
        existing.Keywords = requested.Keywords;
        existing.Headings = requested.Headings;
        existing.Content = requested.Content;
        existing.ProcessingStatus = requested.ProcessingStatus;
        existing.TokenCount = requested.TokenCount;
        existing.ContentHash = requested.ContentHash;
        existing.UpdatedAt = requested.UpdatedAt;

        // Parsing or indexing metadata can change without an audit-worthy content change.
        // Persist those fields while keeping the history free of empty content events.
        if (!contentChanged && !titleChanged)
        {
            await db.SaveChangesAsync(ct);
            await CommitAsync(transaction, ct);
            return existing;
        }

        audit.AppendOrganization(
            existing.OrganizationId,
            existing.TeamId,
            LibraryDocumentAudit.Local(
                LibraryDocumentAudit.Updated,
                AuditChangeType.Edited,
                AuditArea.Content,
                existing,
                existing.Path,
                beforeSnapshot,
                SnapshotForAudit(existing, sourcedUpdate),
                changedFields,
                beforeHash,
                existing.ContentHash
            )
        );
        await db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);
        return existing;
    }

    private async Task DeleteDocumentAsyncCore(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    )
    {
        var normalizedPath = NormalizeLookupPath(path);
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        if (!await LockLibraryKeyShareAsync(organizationId, libraryId, ct))
            return;
        var locked = await LockByPathAsync(organizationId, libraryId, normalizedPath, ct);

        if (locked is null)
        {
            return;
        }

        RememberDocumentPreimage(locked);
        var sourced = await IsSourcedAsync(locked, ct);
        var document = await TrackLockedAsync(locked, ct);
        audit.AppendOrganization(
            organizationId,
            locked.TeamId,
            LibraryDocumentAudit.Local(
                LibraryDocumentAudit.Deleted,
                AuditChangeType.Deleted,
                AuditArea.Content,
                locked,
                locked.Path,
                before: SnapshotForAudit(locked, sourced),
                after: null,
                changedFields: [],
                beforeHash: locked.ContentHash,
                afterHash: null
            )
        );
        db.LibraryDocuments.Remove(document);
        await db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);
    }

    private async Task DeleteDocumentByExternalIdAsyncCore(
        string organizationId,
        string libraryId,
        string sourceExternalId,
        CancellationToken ct
    )
    {
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        if (!await LockLibraryKeyShareAsync(organizationId, libraryId, ct))
            return;
        var locked = await LockByExternalIdAsync(organizationId, libraryId, sourceExternalId, ct);

        if (locked is null)
        {
            return;
        }

        RememberDocumentPreimage(locked);
        var document = await TrackLockedAsync(locked, ct);

        // Synchronized document: record content-free facts only.
        audit.AppendOrganization(
            organizationId,
            locked.TeamId,
            LibraryDocumentAudit.Sync(
                AuditChangeType.Deleted,
                locked,
                locked.Path,
                syncRunId: null,
                before: LibraryDocumentAudit.SyncFacts(
                    locked.Path,
                    characterCount: null,
                    sourceExternalId
                ),
                after: null,
                changedFields: [],
                beforeHash: locked.ContentHash,
                afterHash: null
            )
        );
        db.LibraryDocuments.Remove(document);
        await db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);
    }

    private async Task<LibraryDocument?> SetCodeReviewExclusionAsyncCore(
        string organizationId,
        string libraryId,
        string documentId,
        bool excluded,
        CancellationToken ct
    )
    {
        // The API resolves the loaded document by stable id before calling this mutation.
        // Keep the update keyed on that identity instead of re-running path/suffix/alias
        // resolution; the toggle applies to the specific document currently open in the editor.
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        if (!await LockLibraryKeyShareAsync(organizationId, libraryId, ct))
            return null;
        var document = await LockByIdAsync(organizationId, libraryId, documentId, ct);
        if (document is null)
        {
            return null;
        }

        RememberDocumentPreimage(document);
        if (document.ExcludedFromCodeReviews == excluded)
        {
            // Idempotent repeat: no write, no timestamp churn, no audit fact.
            await CommitAsync(transaction, ct);
            return document;
        }

        var now = DateTimeOffset.UtcNow;
        var sourced = await IsSourcedAsync(document, ct);
        var before = SnapshotForAudit(document, sourced);
        var after = SnapshotForAudit(
            document.Path,
            document.Title,
            document.Content,
            excluded,
            document.AsScopedSkill,
            document.SourceExternalId,
            sourced
        );
        audit.AppendOrganization(
            organizationId,
            document.TeamId,
            LibraryDocumentAudit.Local(
                LibraryDocumentAudit.ReviewExclusionChanged,
                AuditChangeType.Edited,
                AuditArea.ReviewConfiguration,
                document,
                document.Path,
                before,
                after,
                ["excludedFromCodeReviews"],
                document.ContentHash,
                document.ContentHash
            )
        );

        await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.set_code_review_exclusion"
            )
            .Where(row =>
                row.OrganizationId == document.OrganizationId
                && row.LibraryId == document.LibraryId
                && row.Id == document.Id
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.ExcludedFromCodeReviews, excluded)
                        .SetProperty(row => row.UpdatedAt, now),
                ct
            );
        await db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);

        document.ExcludedFromCodeReviews = excluded;
        document.UpdatedAt = now;

        return document;
    }

    private async Task<LibraryDocument?> SetScopedSkillAsyncCore(
        string organizationId,
        string libraryId,
        string documentId,
        LibraryDocumentScopedSkill scopedSkill,
        CancellationToken ct
    )
    {
        await using var transaction = await BeginOwnedTransactionAsync(ct);
        if (!await LockLibraryKeyShareAsync(organizationId, libraryId, ct))
            return null;
        var document = await LockByIdAsync(organizationId, libraryId, documentId, ct);
        if (document is null)
        {
            return null;
        }

        RememberDocumentPreimage(document);
        if (document.AsScopedSkill == scopedSkill)
        {
            await CommitAsync(transaction, ct);
            return document;
        }

        var now = DateTimeOffset.UtcNow;
        var sourced = await IsSourcedAsync(document, ct);
        var before = SnapshotForAudit(document, sourced);
        var after = SnapshotForAudit(
            document.Path,
            document.Title,
            document.Content,
            document.ExcludedFromCodeReviews,
            scopedSkill,
            document.SourceExternalId,
            sourced
        );
        audit.AppendOrganization(
            organizationId,
            document.TeamId,
            LibraryDocumentAudit.Local(
                LibraryDocumentAudit.SkillScopeChanged,
                AuditChangeType.Edited,
                AuditArea.Settings,
                document,
                document.Path,
                before,
                after,
                ["scopedSkill"],
                document.ContentHash,
                document.ContentHash
            )
        );

        await db
            .LibraryDocuments.TagWithOperationCallSite(
                "documents.library_document.set_scoped_skill"
            )
            .Where(row =>
                row.OrganizationId == document.OrganizationId
                && row.LibraryId == document.LibraryId
                && row.Id == document.Id
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(row => row.AsScopedSkill, scopedSkill)
                        .SetProperty(row => row.UpdatedAt, now),
                ct
            );
        await db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);

        document.AsScopedSkill = scopedSkill;
        document.UpdatedAt = now;
        return document;
    }

    private async Task<LibraryDocument?> MoveDocumentAsyncCore(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath,
        CancellationToken ct
    )
    {
        var normalizedTo = DocumentNormalizer.NormalizePath(toPath);

        await using var transaction = await BeginOwnedTransactionAsync(ct);
        if (!await LockLibraryKeyShareAsync(organizationId, libraryId, ct))
            return null;

        // Resolve the source via the same tiered walk used everywhere else, then lock the
        // resolved row by id so the recorded old path is the committed one.
        var resolved = await GetByPathAsync(organizationId, libraryId, fromPath, ct);
        if (resolved is null)
        {
            return null;
        }

        var locked = await LockByIdAsync(organizationId, libraryId, resolved.Id, ct);
        if (locked is null)
        {
            return null;
        }

        RememberDocumentPreimage(locked);
        // NOTE: Idempotent no-op guard. `locked` is the *current* document regardless of whether the
        // caller passed its live path or an old alias, so comparing the resolved live path to the
        // target also short-circuits "rename an old alias back onto the live path" — the case a
        // reviewer flagged as a potential duplicate-alias append. Because we return here, the append
        // below never runs for that scenario, so PreviousPaths cannot accumulate the live path.
        if (locked.Path == normalizedTo)
        {
            await CommitAsync(transaction, ct);
            return locked;
        }

        // Target must be free: not a live path and not an existing alias of *another* doc (D-4).
        // d.Id != doc.Id allows the current document to move back to one of its own previous aliases.
        var collides = await db
            .LibraryDocuments.AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.move_collision_check")
            .AnyAsync(
                d =>
                    d.OrganizationId == organizationId
                    && d.LibraryId == libraryId
                    && d.Id != locked.Id
                    && (d.Path == normalizedTo || d.PreviousPaths.Contains(normalizedTo)),
                ct
            );

        if (collides)
        {
            throw new DuplicateDocumentPathException(normalizedTo);
        }

        var sourced = await IsSourcedAsync(locked, ct);
        var before = SnapshotForAudit(locked, sourced);
        var doc = await TrackLockedAsync(locked, ct);
        doc.RenameTo(normalizedTo);
        audit.AppendOrganization(
            organizationId,
            doc.TeamId,
            LibraryDocumentAudit.Local(
                LibraryDocumentAudit.Renamed,
                AuditChangeType.Edited,
                AuditArea.Content,
                doc,
                doc.Path,
                before,
                SnapshotForAudit(doc, sourced),
                ["path"],
                doc.ContentHash,
                doc.ContentHash
            )
        );
        await db.SaveChangesAsync(ct);
        await CommitAsync(transaction, ct);

        return doc;
    }
}
