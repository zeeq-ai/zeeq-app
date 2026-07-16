using System.Security.Cryptography;
using Zeeq.Core.Documents.Parsing;

namespace Zeeq.Core.Documents;

/// <summary>
/// Parses markdown and writes normalized library documents through <see cref="ILibraryDocumentStore"/>.
/// </summary>
/// <param name="store">The persistence abstraction used for document lookup and upsert.</param>
public sealed class LibraryDocumentWriteService(ILibraryDocumentStore store)
{
    /// <summary>
    /// Upserts a markdown document into a library.
    /// </summary>
    /// <param name="organizationId">Organization that owns the target library.</param>
    /// <param name="teamId">Optional team owner to assign when the document is first created.</param>
    /// <param name="libraryId">Library that contains the document.</param>
    /// <param name="rawPath">Caller-supplied path before normalization.</param>
    /// <param name="markdownSource">Full markdown source to parse and persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted, updated, or unchanged document.</returns>
    /// <remarks>
    /// Existing documents are matched by normalized path. If the markdown source hash is unchanged,
    /// the stored row is returned without updating timestamps or processing status.
    /// </remarks>
    public async Task<LibraryDocument> UpsertDocumentAsync(
        string organizationId,
        string? teamId,
        string libraryId,
        string rawPath,
        string markdownSource,
        CancellationToken ct
    )
    {
        var normalizedPath = DocumentNormalizer.NormalizePath(rawPath);
        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        var parsed = MarkdownParser.Parse(markdownSource, fileName);
        var contentHash = ComputeSha256Hex(markdownSource);

        // NOTE: Document identity is organization + library + path. The Postgres schema enforces
        // that key as unique, so team-scoped duplicates inside the same library cannot exist.
        var existing = await store.GetByPathAsync(organizationId, libraryId, normalizedPath, ct);

        if (existing is not null && existing.ContentHash == contentHash)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var document =
            existing
            ?? new LibraryDocument
            {
                Id = NewId(),
                OrganizationId = organizationId,
                TeamId = teamId,
                LibraryId = libraryId,
                Path = normalizedPath,
                CreatedAt = now,
            };

        // NOTE: TeamId is immutable after document creation. Moving a document between teams
        // should be an explicit delete/recreate flow so team-scoped ownership cannot drift on update.

        document.Title = parsed.Title;
        document.TitleNormalized = DocumentNormalizer.Normalize(parsed.Title);
        document.Keywords = DocumentNormalizer.NormalizeKeywords(parsed.Keywords);
        document.Headings = [.. parsed.Headings];
        document.Content = markdownSource;
        document.ContentHash = contentHash;
        document.TokenCount = TiktokenCounter.CountTokens(parsed.Content);
        document.ProcessingStatus = DocumentProcessingStatus.Pending;
        document.UpdatedAt = now;

        return await store.UpsertDocumentAsync(document, ct);
    }

    private static string ComputeSha256Hex(string content) =>
        Convert
            .ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private static string NewId() => $"document_{Guid.CreateVersion7():N}";
}
