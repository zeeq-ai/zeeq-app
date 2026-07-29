namespace Zeeq.Core.Documents;

/// <summary>
/// Projection for a library document exposed as a scoped skill.
/// </summary>
/// <remarks>
/// <c>UpdatedAt</c> carries the source document's last-modified timestamp so callers can build
/// version-stamped cache keys. The MCP retrieval path caches the rendered prompt body per
/// (document, repository); putting this stamp in the key means a document edit produces a new key
/// and stale entries are simply never read, instead of requiring eviction hooks on every document
/// write path.
/// </remarks>
public sealed record LibraryScopedSkillDocument(
    string OrganizationId,
    string LibraryId,
    string LibraryName,
    string DocumentId,
    string Path,
    string Title,
    string? ManualSkillName,
    string? ParsedSkillName,
    string? ManualSkillDescription,
    string? ParsedSkillDescription,
    DocumentMetadata? Metadata,
    string? Content,
    DateTimeOffset UpdatedAt
);
