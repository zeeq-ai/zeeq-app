using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Builds the allowlisted audit snapshots and facts for library document mutations.
/// </summary>
/// <remarks>
/// Local (hand-authored) documents record full before/after snapshots so an editor can compare,
/// copy, or download either side. Synchronized documents record facts only — hashes, sizes, and
/// source references — upstream history is outside this store's recovery contract. A source link
/// does not guarantee that the provider retains old text or that the reader can still access it.
/// Operational fields
/// (processing status, token counts, timestamps, sync stamps) are never snapshotted.
/// </remarks>
internal static class LibraryDocumentAudit
{
    internal const string Created = "document.created";
    internal const string Updated = "document.updated";
    internal const string Deleted = "document.deleted";
    internal const string Renamed = "document.renamed";
    internal const string ReviewExclusionChanged = "document.review_exclusion_changed";
    internal const string SkillScopeChanged = "document.skill_scope_changed";
    internal const string Synced = "document.synced";

    private static readonly AuditActor SyncActor = AuditActor.Worker("Content sync");

    internal static AuditSnapshot Snapshot(LibraryDocument document) =>
        Snapshot(
            document.Path,
            document.Title,
            document.Content,
            document.ExcludedFromCodeReviews,
            document.AsScopedSkill
        );

    internal static AuditSnapshot Snapshot(
        string path,
        string title,
        string content,
        bool excludedFromCodeReviews,
        LibraryDocumentScopedSkill scopedSkill
    ) =>
        new()
        {
            Path = path,
            Title = title,
            Content = content,
            Fields = new Dictionary<string, string?>
            {
                ["excludedFromCodeReviews"] = excludedFromCodeReviews ? "true" : "false",
                ["scopedSkill"] = scopedSkill.ToString(),
            },
        };

    /// <summary>
    /// Creates a local-document fact. The subject label is the event-time path so later renames
    /// do not rewrite history.
    /// </summary>
    internal static AuditChange Local(
        string eventType,
        AuditChangeType changeType,
        AuditArea area,
        LibraryDocument subject,
        string subjectPath,
        AuditSnapshot? before,
        AuditSnapshot? after,
        IReadOnlyList<string> changedFields,
        string? beforeHash,
        string? afterHash
    ) =>
        new()
        {
            EventType = eventType,
            ChangeType = changeType,
            Area = area,
            SubjectType = "document",
            SubjectId = subject.Id,
            SubjectLabel = subjectPath,
            LibraryId = subject.LibraryId,
            Before = before,
            After = after,
            ChangedFields = changedFields,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
        };

    /// <summary>
    /// Creates a synchronized-document fact: path, content hash, and size only.
    /// </summary>
    internal static AuditChange Sync(
        AuditChangeType changeType,
        LibraryDocument subject,
        string subjectPath,
        string? syncRunId,
        AuditSnapshot? before,
        AuditSnapshot? after,
        IReadOnlyList<string> changedFields,
        string? beforeHash,
        string? afterHash
    ) =>
        new()
        {
            EventType = Synced,
            ChangeType = changeType,
            Area = AuditArea.Content,
            SubjectType = "document",
            SubjectId = subject.Id,
            SubjectLabel = subjectPath,
            LibraryId = subject.LibraryId,
            Before = before,
            After = after,
            ChangedFields = changedFields,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            ActorOverride = SyncActor,
            SourceReference = SyncSourceReference(syncRunId, subject.SourceExternalId),
            OperationId = SyncOperationId(syncRunId),
        };

    /// <summary>
    /// Content-free facts for a synchronized document.
    /// </summary>
    internal static AuditSnapshot SyncFacts(string path, int? characterCount, string? externalId)
    {
        var fields = new Dictionary<string, string?>();
        if (characterCount is { } count)
        {
            fields["characterCount"] = count.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        if (externalId is not null)
        {
            fields["externalId"] = externalId;
        }

        return new AuditSnapshot { Path = path, Fields = fields };
    }

    internal static string SyncSourceReference(string? syncRunId, string? externalId) =>
        syncRunId is not null ? $"sync-run:{syncRunId}"
        : externalId is not null ? $"external-id:{externalId}"
        : "sync";

    // Only the current trusted run identifies separately committed child facts. A document's
    // old sync stamp or a fallback request ID must not imply membership in the run now deleting it.
    internal static Guid? SyncOperationId(string? syncRunId) =>
        syncRunId is not null
        && syncRunId.StartsWith("run_", StringComparison.Ordinal)
        && Guid.TryParseExact(syncRunId.AsSpan(4), "N", out var id)
            ? id
            : null;
}
