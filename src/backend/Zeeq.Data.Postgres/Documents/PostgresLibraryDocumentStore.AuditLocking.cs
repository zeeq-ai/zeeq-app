using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

// Shared snapshot policy and row locks; all parts use the same scoped DbContext and audit writer.
internal sealed partial class PostgresLibraryDocumentStore
{
    // --- Audit support -----------------------------------------------------------------------

    // NOTE: Local rows in sourced libraries still need full, revertible snapshots.
    // Only rows marked Synced are redacted; library provenance alone is insufficient.
    private static bool IsSourced(LibraryDocument document) =>
        document.Authoring == LibraryDocumentAuthoring.Synced;

    private static AuditSnapshot SnapshotForAudit(LibraryDocument document, bool sourced) =>
        SnapshotForAudit(
            document.Path,
            document.Title,
            document.Content,
            document.ExcludedFromCodeReviews,
            document.AsScopedSkill,
            document.SourceExternalId,
            document.Authoring,
            sourced
        );

    private static AuditSnapshot SnapshotForAudit(
        string path,
        string title,
        string content,
        bool excluded,
        LibraryDocumentScopedSkill skill,
        string? externalId,
        LibraryDocumentAuthoring authoring,
        bool sourced
    ) =>
        sourced
            ? LibraryDocumentAudit.SyncFacts(path, content.Length, externalId) with
            {
                Fields = new Dictionary<string, string?>
                {
                    ["characterCount"] = content.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                    ["externalId"] = externalId,
                    ["excludedFromCodeReviews"] = excluded ? "true" : "false",
                    ["scopedSkill"] = skill.ToString(),
                    ["authoring"] = authoring.ToString(),
                },
            }
            : LibraryDocumentAudit.Snapshot(path, title, content, excluded, skill, authoring);

    /// <summary>
    /// Starts a transaction unless the caller already owns one, in which case the mutation and
    /// its audit fact commit with the caller's unit of work.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken ct) =>
        db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;

    private static Task CommitAsync(IDbContextTransaction? transaction, CancellationToken ct) =>
        transaction is null ? Task.CompletedTask : transaction.CommitAsync(ct);

    // Every document mutation takes the parent lock first. KEY SHARE conflicts with library
    // deletion but allows ordinary document writes and sync-state updates to proceed concurrently.
    private async Task<bool> LockLibraryKeyShareAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) =>
        (
            await db
                .Database.SqlQuery<string>(
                    $"""
                    SELECT id AS "Value" FROM zeeq.docs_libraries
                    WHERE organization_id = {organizationId} AND id = {libraryId} FOR KEY SHARE
                    """
                )
                .ToArrayAsync(ct)
        ).Length != 0;

    private async Task<LibraryDocument?> LockByPathAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) =>
        (
            await db
                .LibraryDocuments.FromSql(
                    $"""
                    SELECT * FROM zeeq.docs_library_documents
                    WHERE organization_id = {organizationId}
                      AND library_id = {libraryId}
                      AND path = {path}
                    FOR UPDATE
                    """
                )
                .AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.lock_by_path")
                .ToArrayAsync(ct)
        ).SingleOrDefault();

    private async Task<LibraryDocument?> LockLocalByPathAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) =>
        (
            await db
                .LibraryDocuments.FromSql(
                    $"""
                    SELECT * FROM zeeq.docs_library_documents
                    WHERE organization_id = {organizationId} AND library_id = {libraryId}
                      AND path = {path} AND authoring <> 'Synced'
                    LIMIT 1 FOR UPDATE
                    """
                )
                .AsNoTracking()
                .ToArrayAsync(ct)
        ).FirstOrDefault();

    private async Task<LibraryDocument?> LockByIdAsync(
        string organizationId,
        string libraryId,
        string documentId,
        CancellationToken ct
    ) =>
        (
            await db
                .LibraryDocuments.FromSql(
                    $"""
                    SELECT * FROM zeeq.docs_library_documents
                    WHERE organization_id = {organizationId}
                      AND library_id = {libraryId}
                      AND id = {documentId}
                    FOR UPDATE
                    """
                )
                .AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.lock_by_id")
                .ToArrayAsync(ct)
        ).SingleOrDefault();

    private async Task<LibraryDocument?> LockByExternalIdAsync(
        string organizationId,
        string libraryId,
        string sourceExternalId,
        CancellationToken ct
    ) =>
        (
            await db
                .LibraryDocuments.FromSql(
                    $"""
                    SELECT * FROM zeeq.docs_library_documents
                    WHERE organization_id = {organizationId}
                      AND library_id = {libraryId}
                      AND source_external_id = {sourceExternalId}
                    FOR UPDATE
                    """
                )
                .AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.lock_by_external_id")
                .ToArrayAsync(ct)
        ).SingleOrDefault();

    private async Task<LibraryDocument[]> LockByContentHashAsync(
        string organizationId,
        string libraryId,
        string? contentHash,
        CancellationToken ct
    ) =>
        await db
            .LibraryDocuments.FromSql(
                $"""
                SELECT * FROM zeeq.docs_library_documents
                WHERE organization_id = {organizationId}
                  AND library_id = {libraryId}
                  AND content_hash = {contentHash}
                  AND authoring = 'Synced'
                LIMIT 2
                FOR UPDATE
                """
            )
            .AsNoTracking()
            .TagWithOperationCallSite("documents.library_document.lock_by_content_hash")
            .ToArrayAsync(ct);

    /// <summary>
    /// Starts tracking a row that was read (and locked) with <c>AsNoTracking</c>. When the context
    /// already tracks the row, reloads it so a stale in-memory copy cannot hide a column change.
    /// </summary>
    private async Task<LibraryDocument> TrackLockedAsync(
        LibraryDocument locked,
        CancellationToken ct
    )
    {
        var entry = db
            .ChangeTracker.Entries<LibraryDocument>()
            .FirstOrDefault(tracked =>
                tracked.Entity.OrganizationId == locked.OrganizationId
                && tracked.Entity.LibraryId == locked.LibraryId
                && tracked.Entity.Id == locked.Id
            );
        if (entry is null)
        {
            db.LibraryDocuments.Attach(locked);
            return locked;
        }

        await entry.ReloadAsync(ct);
        return entry.Entity;
    }
}
