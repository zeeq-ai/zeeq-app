namespace Zeeq.Core.Documents;

/// <summary>
/// Projection for a library document exposed as a scoped skill.
/// </summary>
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
    string? Content
);
