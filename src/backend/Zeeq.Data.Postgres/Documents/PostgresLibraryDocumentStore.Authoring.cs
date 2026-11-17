using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

internal sealed partial class PostgresLibraryDocumentStore
{
    /// <inheritdoc />
    public async Task<CreateDocumentResult> CreateAgentDocumentAsync(
        LibraryDocument document,
        string rationale,
        AuditActor actor,
        string? clientName,
        CancellationToken ct
    )
    {
        if (document.SourceExternalId is not null || document.SourceOrigin is not null)
            throw new ArgumentException(
                "Agent documents cannot carry a sync source.",
                nameof(document)
            );
        if (db.Entry(document).State != EntityState.Detached)
            throw new ArgumentException("Create requires an untracked document.", nameof(document));
        var originalAuthoring = document.Authoring;
        var originalHash = document.ContentHash;
        var created = false;
        try
        {
            var result = await RunDocumentMutationAsync<CreateDocumentResult>(
                async () =>
                {
                    if (
                        !await LockLibraryKeyShareAsync(
                            document.OrganizationId,
                            document.LibraryId,
                            ct
                        )
                    )
                        throw new InvalidOperationException("Library does not exist.");
                    var existing = await LockAnyByPathAsync(
                        document.OrganizationId,
                        document.LibraryId,
                        document.Path,
                        ct
                    );
                    if (existing is not null)
                        return new CreateDocumentResult.Exists(existing);
                    document.Authoring = LibraryDocumentAuthoring.Agent;
                    document.ContentHash = DocumentContentHash.Compute(document.Content);
                    audit.AppendOrganization(
                        document.OrganizationId,
                        document.TeamId,
                        LibraryDocumentAudit.Local(
                            LibraryDocumentAudit.Created,
                            AuditChangeType.Added,
                            AuditArea.Content,
                            document,
                            document.Path,
                            null,
                            WithRationale(
                                LibraryDocumentAudit.Snapshot(document),
                                rationale,
                                clientName
                            ),
                            [],
                            null,
                            document.ContentHash
                        ) with
                        {
                            ActorOverride = actor,
                        }
                    );
                    db.LibraryDocuments.Add(document);
                    await db.SaveChangesAsync(ct);
                    return new CreateDocumentResult.Created(document);
                },
                document,
                ct
            );
            created = result is CreateDocumentResult.Created;
            return result;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException
                    is PostgresException
                    {
                        SqlState: PostgresErrorCodes.UniqueViolation,
                        ConstraintName: "ix_docs_library_documents_path"
                    }
            )
        {
            // The checkpoint has rolled back the failed insert and its audit before re-reading.
            var existing = await FindByExactPathAsync(
                document.OrganizationId,
                document.LibraryId,
                document.Path,
                ct
            );
            if (existing is null)
                throw;
            return new CreateDocumentResult.Exists(existing);
        }
        finally
        {
            if (!created)
            {
                document.Authoring = originalAuthoring;
                document.ContentHash = originalHash;
            }
        }
    }

    /// <inheritdoc />
    public Task<ReplaceContentResult> TryReplaceContentAsync(
        LibraryDocument document,
        string expectedHash,
        string rationale,
        AuditActor actor,
        string? clientName,
        CancellationToken ct
    )
    {
        // Capture requested values before a tracked document is reloaded under the row lock.
        var requested = (
            document.Title,
            document.TitleNormalized,
            document.ParsedSkillName,
            document.ParsedSkillDescription,
            document.Keywords,
            document.Headings,
            document.Content,
            document.ContentHash,
            document.TokenCount,
            document.ProcessingStatus,
            document.UpdatedAt
        );
        return RunDocumentMutationAsync<ReplaceContentResult>(
            async () =>
            {
                if (
                    !await LockLibraryKeyShareAsync(document.OrganizationId, document.LibraryId, ct)
                )
                {
                    ResetTrackedAuthoringInput(document, current: null);
                    return new ReplaceContentResult.NotFound();
                }
                var locked = await LockByIdAsync(
                    document.OrganizationId,
                    document.LibraryId,
                    document.Id,
                    ct
                );
                if (locked is null || locked.Path != document.Path)
                {
                    var current = await LockAnyByPathAsync(
                        document.OrganizationId,
                        document.LibraryId,
                        document.Path,
                        ct
                    );
                    // NOTE: The locked row is the same document identity even when its path
                    // moved; refresh that tracked input without replaying the rejected edit.
                    ResetTrackedAuthoringInput(document, locked);
                    return current is null
                        ? new ReplaceContentResult.NotFound()
                        : new ReplaceContentResult.Stale(current);
                }
                if (
                    locked.Authoring == LibraryDocumentAuthoring.Synced
                    || locked.SourceExternalId is not null
                    || locked.SourceOrigin is not null
                )
                {
                    ResetTrackedAuthoringInput(document, locked);
                    return new ReplaceContentResult.NotEditable(locked);
                }
                if (locked.ContentHash != expectedHash)
                {
                    ResetTrackedAuthoringInput(document, locked);
                    return new ReplaceContentResult.Stale(locked);
                }
                // No content change means no mutation or audit event. A tracked request may
                // still have unrelated dirty values on the target, so discard those too.
                if (locked.Content == requested.Content)
                {
                    ResetTrackedAuthoringInput(document, locked);
                    return new ReplaceContentResult.Applied(locked);
                }
                RememberDocumentPreimage(locked);
                var before = LibraryDocumentAudit.Snapshot(locked);
                var beforeHash = locked.ContentHash;
                var existing = await TrackLockedAsync(locked, ct);
                existing.Title = requested.Title;
                existing.TitleNormalized = requested.TitleNormalized;
                existing.ParsedSkillName = requested.ParsedSkillName;
                existing.ParsedSkillDescription = requested.ParsedSkillDescription;
                existing.Keywords = requested.Keywords;
                existing.Headings = requested.Headings;
                existing.Content = requested.Content;
                existing.ContentHash = DocumentContentHash.Compute(requested.Content);
                existing.TokenCount = requested.TokenCount;
                existing.ProcessingStatus = requested.ProcessingStatus;
                existing.UpdatedAt = requested.UpdatedAt;
                audit.AppendOrganization(
                    existing.OrganizationId,
                    existing.TeamId,
                    LibraryDocumentAudit.Local(
                        LibraryDocumentAudit.Updated,
                        AuditChangeType.Edited,
                        AuditArea.Content,
                        existing,
                        existing.Path,
                        before,
                        WithRationale(
                            LibraryDocumentAudit.Snapshot(existing),
                            rationale,
                            clientName
                        ),
                        ["content"],
                        beforeHash,
                        existing.ContentHash
                    ) with
                    {
                        ActorOverride = actor,
                    }
                );
                await db.SaveChangesAsync(ct);
                return new ReplaceContentResult.Applied(existing);
            },
            document,
            ct
        );
    }

    // A rejected tracked request must not be replayed by a later SaveChanges. Reset only
    // the attempted target; caller changes to other tracked entities remain pending.
    private void ResetTrackedAuthoringInput(LibraryDocument input, LibraryDocument? current)
    {
        var entry = db
            .ChangeTracker.Entries<LibraryDocument>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Entity, input));
        if (entry is null)
            return;
        if (current is null)
        {
            entry.State = EntityState.Detached;
            return;
        }
        entry.CurrentValues.SetValues(current);
        entry.OriginalValues.SetValues(current);
        entry.State = EntityState.Unchanged;
    }

    private async Task<LibraryDocument?> LockAnyByPathAsync(
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
                    WHERE organization_id = {organizationId} AND library_id = {libraryId} AND path = {path}
                    ORDER BY (authoring = 'Synced') LIMIT 1 FOR UPDATE
                    """
                )
                .AsNoTracking()
                .TagWithOperationCallSite("documents.library_document.authoring_lock_by_path")
                .ToArrayAsync(ct)
        ).FirstOrDefault();

    private static AuditSnapshot WithRationale(
        AuditSnapshot snapshot,
        string rationale,
        string? clientName
    ) =>
        snapshot with
        {
            Fields = new Dictionary<string, string?>(snapshot.Fields)
            {
                ["rationale"] = rationale,
                ["mcpClientName"] = clientName,
            },
        };
}
