using Zeeq.Core.Documents.Parsing;

namespace Zeeq.Core.Documents;

/// <summary>Prepares markdown and its searchable metadata for a document write.</summary>
public static class DocumentContentPreparation
{
    /// <summary>Rebuilds content-derived fields without changing identity or ownership.</summary>
    public static void Apply(LibraryDocument document, string content, DateTimeOffset now)
    {
        var parsed = MarkdownParser.Parse(content, Path.GetFileNameWithoutExtension(document.Path));
        document.Title = parsed.Title;
        document.TitleNormalized = DocumentNormalizer.Normalize(parsed.Title);
        document.ParsedSkillName = DocumentNormalizer.NormalizeOptionalPromptName(
            parsed.ParsedSkillName
        );
        document.ParsedSkillDescription = DocumentNormalizer.BoundOptionalSkillDescription(
            parsed.ParsedSkillDescription
        );
        document.Keywords = DocumentNormalizer.NormalizeKeywords(parsed.Keywords);
        document.Headings = [.. parsed.Headings];
        document.Content = content;
        document.ContentHash = DocumentContentHash.Compute(content);
        document.TokenCount = TiktokenCounter.CountTokens(parsed.Content);
        document.ProcessingStatus = DocumentProcessingStatus.Pending;
        document.UpdatedAt = now;
    }
}
