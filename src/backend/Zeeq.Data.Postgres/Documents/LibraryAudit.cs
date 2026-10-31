using System.Text.Json;
using Zeeq.Core.Auditing;
using Zeeq.Core.Documents;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Documents;

internal static class LibraryAudit
{
    internal static AuditSnapshot Snapshot(
        Library library,
        int? documentCount = null,
        int? mappingCount = null
    )
    {
        var fields = new Dictionary<string, string?>
        {
            ["name"] = library.Name,
            ["description"] = library.Description,
            ["includeFilters"] = JsonSerializer.Serialize(library.IncludeFilters),
            ["excludeFilters"] = JsonSerializer.Serialize(library.ExcludeFilters),
            ["sourceKind"] = library.SourceKind,
            ["publicSourceId"] = library.PublicSourceId,
        };
        // Counts keep the parent snapshot bounded. Each affected relationship has its own fact.
        if (documentCount is not null)
            fields["documentCount"] = documentCount.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        if (mappingCount is not null)
            fields["repositoryMappingCount"] = mappingCount.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        return new AuditSnapshot { Fields = fields };
    }

    internal static AuditChange Change(
        string eventType,
        AuditChangeType type,
        Library library,
        AuditSnapshot? before,
        AuditSnapshot? after,
        IReadOnlyList<string> changedFields
    ) =>
        new()
        {
            EventType = eventType,
            ChangeType = type,
            Area = AuditArea.Settings,
            SubjectType = "library",
            SubjectId = library.Id,
            SubjectLabel = library.Name,
            LibraryId = library.Id,
            Before = before,
            After = after,
            ChangedFields = changedFields,
        };

    internal static AuditChange Mapping(Library library, CodeRepository repository, bool mapped) =>
        new()
        {
            EventType = "repository.library_mapping_changed",
            ChangeType = AuditChangeType.Edited,
            Area = AuditArea.Settings,
            SubjectType = "repository",
            SubjectId = repository.Id,
            SubjectLabel = repository.DisplayName,
            LibraryId = library.Id,
            Before = MappingSnapshot(library, repository, !mapped),
            After = MappingSnapshot(library, repository, mapped),
            ChangedFields = ["mapped"],
        };

    private static AuditSnapshot MappingSnapshot(
        Library library,
        CodeRepository repository,
        bool mapped
    ) =>
        new()
        {
            Fields = new Dictionary<string, string?>
            {
                ["libraryId"] = library.Id,
                ["libraryName"] = library.Name,
                ["repositoryId"] = repository.Id,
                ["repositoryName"] = repository.DisplayName,
                ["mapped"] = mapped ? "true" : "false",
            },
        };
}
